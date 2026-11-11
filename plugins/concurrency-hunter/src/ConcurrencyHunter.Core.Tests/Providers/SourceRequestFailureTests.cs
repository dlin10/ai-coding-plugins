using System.Net;
using System.Net.Http;
using System.Text.Json;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed partial class SourceRequestFailureTests
{
    public static IEnumerable<object?[]> StatusCases()
    {
        foreach (var stage in new[] { "microsoft", "nuget", "document" })
        foreach (var status in new int?[] { 401, 403, 407, 404, 410, 429, 500, 503, null })
        foreach (var channel in new[] { "response", "exception", "tunnel" })
            if (status is not null || channel != "response")
                yield return [stage, status, channel];
    }

    [Theory]
    [MemberData(nameof(StatusCases))]
    public async Task Status_controls_classification_host_skip_and_finality_on_every_fetch_path(string stage, int? status, string channel)
    {
        var document = stage == "document";
        var host = stage + ".invalid";
        using var first = new SourceLibrary(new Uri("https://document.invalid/"), adjacent: document, documentCount: 1);
        using var second = new SourceLibrary(new Uri("https://document.invalid/"), adjacent: document, documentCount: 1, crlf: true);
        using var handler = new FailureHandler(host, status, channel);
        using var sources = new LibrarySources(false, first.Cache, new Uri("https://microsoft.invalid/"),
            new Uri("https://nuget.invalid/"), timeout: Timeout.InfiniteTimeSpan, handler: handler) { RequestTime = new SourceRetryClock() };
        var stable = status is 401 or 403 or 407 or 404 or 410;
        foreach (var library in new[] { first, second })
        {
            var result = await sources.GetAsync(library.Dll);
            Assert.Equal(stable ? LibraryCodeKind.DecompiledBest : LibraryCodeKind.DecompiledForRun, result.Kind);
            Assert.Equal(stable ? document ? LibraryCodeReason.PersistentFailure : LibraryCodeReason.NoPdb
                : LibraryCodeReason.TemporaryFailure, result.Reason);
            Assert.Equal(!stable, LibrarySourceDecisions.ForCompilation(result).Kind == LibraryCodeKind.DecompiledForRun);
            // All-persistent PDB failures choose the first server, even when NuGet supplies a different status.
            var decidingHost = stage == "nuget" && stable ? "microsoft.invalid" : host;
            var decidingStatus = stage == "nuget" && stable ? 404 : status;
            Assert.Contains("https://" + decidingHost + "/", result.Detail);
            Assert.Contains("(host " + decidingHost + ")", result.Detail);
            Assert.Contains(decidingStatus is null ? "connection error:" : "HTTP " + decidingStatus, result.Detail);
            Assert.Equal(result.Detail, LibrarySourceDecisions.ForCompilation(result).Detail);
        }
        Assert.Equal(status is 401 or 403 or 407 ? 1 : status is 429 or 503 ? 8 : 2, handler.Requests.Count(uri => uri.Host == host));
    }

    public static IEnumerable<object?[]> StorageCases()
    {
        foreach (var stage in new[] { "microsoft", "nuget", "document" })
        foreach (var status in new int?[] { 407, 503, null })
        foreach (var channel in new[] { "response", "exception" })
            if (status is not null || channel != "response")
                yield return [stage, status, channel];
        yield return ["offline", null, "exception"];
    }

    [Theory]
    [MemberData(nameof(StorageCases))]
    public void Deciding_request_reaches_the_generated_answer_and_controls_storage(string stage, int? status, string channel)
    {
        var document = stage == "document";
        using var library = new SourceLibrary(new Uri("https://document.invalid/"), adjacent: document, documentCount: 1);
        using var install = new GenerationInstall();
        var directory = install.SharedFramework("8.0.5", runtime: true,
            images: new Dictionary<string, byte[]> { ["library"] = File.ReadAllBytes(library.Dll) });
        if (document)
            File.Copy(library.Pdb, Path.Combine(directory, "library.pdb"));
        using var handler = new FailureHandler(stage + ".invalid", status, channel);
        using var sources = new LibrarySources(stage == "offline", library.Cache, new Uri("https://microsoft.invalid/"),
            new Uri("https://nuget.invalid/"), timeout: Timeout.InfiniteTimeSpan, handler: handler) { RequestTime = new SourceRetryClock() };
        var compilation = CSharpCompilation.Create("App", references:
            [.. EmittedAssemblies.RuntimeReferences, MetadataReference.CreateFromFile(Path.Combine(directory, "library.dll"))]);
        var member = compilation.GetTypeByMetadataName("C0")!.GetMembers("M").OfType<IMethodSymbol>().Single();
        var target = Assert.IsType<GenerationTarget>(GenerationTargets.Of(member, compilation, install.Resolver(),
            CancellationToken.None, generationFrameworkMajor: 8));
        var candidate = new GenerationCandidate(compilation, member, target, false);
        var repository = Path.Combine(install.Root, "repository");
        using var pass = GenerationPass.Create(new GenerationSetup(ModelGeneration.Generate, install.Resolver(), Sources: sources),
            repository, CancellationToken.None)!;
        var layer = pass.Prepare(LibraryModels.BuiltIn, [candidate]);
        Assert.Equal(1, pass.GeneratedInRun([candidate]));
        Assert.Equal(StoredGeneratedKind.Model,
            Assert.IsType<StoredGeneratedAnswer>(layer.Table.Lookup(compilation, member, target.LookupId)).Kind);
        Assert.Equal(status == 407 && stage != "offline", GeneratedModelFiles.Read(repository, target.Key).Entries.ContainsKey(target.MemberId));

        var request = new GenerationRequest("library", "8.0", "M:C0.M", null, null);
        var ownership = new CompilationOwnership();
        GeneratedAnswer answer;
        try
        {
            using var owned = ownership.Open();
            answer = ModelGenerator.GenerateWithin(request, install.Resolver(), sources: sources);
        }
        finally { LibraryCompilation.Release(ownership.Created); }
        using var json = JsonDocument.Parse(GeneratedDocument.Write(answer));
        var code = json.RootElement.GetProperty("generation").GetProperty("code");
        Assert.Equal(5, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(answer.Generation.Code!.Detail, code.GetProperty("detail").GetString());
        if (stage == "offline")
        {
            Assert.Equal("offline", code.GetProperty("detail").GetString());
            Assert.Empty(handler.Requests);
        }
        else
        {
            var decidingHost = stage == "nuget" && status == 407 ? "microsoft" : stage;
            Assert.Contains("https://" + decidingHost + ".invalid/", code.GetProperty("detail").GetString());
            var decidingStatus = stage == "nuget" && status == 407 ? 404 : status;
            Assert.Contains(decidingStatus is null ? "connection error:" : "HTTP " + decidingStatus, code.GetProperty("detail").GetString());
        }
    }

    [Theory]
    [InlineData(500, 429, false, "file0.cs", LibraryCodeReason.TemporaryFailure)]
    [InlineData(500, 404, false, "file1.cs", LibraryCodeReason.PersistentFailure)]
    [InlineData(404, 500, true, "file1.cs", LibraryCodeReason.NoSources)]
    public async Task Detail_follows_reason_precedence_and_first_failure_with_that_outcome(int first, int second,
        bool partialLinks, string decidingDocument, LibraryCodeReason reason)
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address, adjacent: true, partialSourceLink: partialLinks);
        server.Replies["/sources/file0.cs"] = new(first);
        server.Replies["/sources/file1.cs"] = new(second);
        using var sources = new LibrarySources(false, library.Cache, httpsOnly: false, timeout: Timeout.InfiniteTimeSpan)
        { RequestTime = new SourceRetryClock() };
        var result = await sources.GetAsync(library.Dll);
        Assert.Equal(reason, result.Reason);
        Assert.Contains(decidingDocument, result.Detail);
        Assert.DoesNotContain(decidingDocument == "file0.cs" ? "file1.cs" : "file0.cs", result.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_detail_identifies_the_request_including_a_stalled_body(bool body)
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address, adjacent: true);
        server.Replies["/sources/file0.cs"] = body ? new(Bytes: [1], StallBody: true) : new(Delay: Timeout.InfiniteTimeSpan);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RequestReceived = _ => { if (!body) waiting.TrySetResult(); };
        server.BodyStalled = _ => waiting.TrySetResult();
        var clock = new FakeClock();
        using var sources = new LibrarySources(false, library.Cache, httpsOnly: false, timeout: TimeSpan.FromMilliseconds(250)) { RequestTime = clock };
        var read = sources.GetAsync(library.Dll);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(read.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var result = await read.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(LibraryCodeReason.TemporaryFailure, result.Reason);
        Assert.Equal(new Uri(server.Address, "sources/file0.cs").AbsoluteUri + " (host " + server.Address.Host + "): no answer in 0.25 s", result.Detail);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Methodless_failure_decides_detail_only_when_sources_are_rejected()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address, adjacent: true, optionalDocument: true);
        library.Serve(server);
        server.Replies["/sources/AssemblyInfo.cs"] = new(503);
        using var sources = new LibrarySources(false, library.Cache, httpsOnly: false, timeout: Timeout.InfiniteTimeSpan)
        { RequestTime = new SourceRetryClock() };
        var result = await sources.GetAsync(library.Dll);
        Assert.Equal(LibraryCodeKind.OriginalSources, result.Kind);
        Assert.Null(result.Detail);
        Assert.Null(LibrarySourceDecisions.ForCompilation(result).Detail);
        var fallback = LibrarySourceDecisions.ForCompilation(result, sourcesRejected: true);
        Assert.Equal(LibraryCodeReason.TemporaryFailure, fallback.Reason);
        Assert.Contains("/sources/AssemblyInfo.cs", fallback.Detail);
        Assert.Contains("HTTP 503", fallback.Detail);
    }

    private sealed class FailureHandler(string host, int? status, string channel) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host != host)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (channel != "response")
                throw new HttpRequestException(channel == "tunnel" ? HttpRequestError.ProxyTunnelError : HttpRequestError.ConnectionError,
                    "loopback connection failure", statusCode: (HttpStatusCode?)status);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status!));
        }
    }
}
