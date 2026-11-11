using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class LibrarySourcesTests
{
    [Fact]
    public void Host_classification_follows_the_stage_table()
    {
        var cases = new (int? Status, HostFailure Failure, SourceOutcome Outcome, bool Skip)[]
        {
            (200, HostFailure.None, SourceOutcome.Received, false),
            (404, HostFailure.None, SourceOutcome.Persistent, false),
            (410, HostFailure.None, SourceOutcome.Persistent, false),
            (401, HostFailure.None, SourceOutcome.Persistent, true),
            (403, HostFailure.None, SourceOutcome.Persistent, true),
            (407, HostFailure.None, SourceOutcome.Persistent, true),
            (429, HostFailure.None, SourceOutcome.Transient, false),
            (500, HostFailure.None, SourceOutcome.Transient, false),
            (503, HostFailure.None, SourceOutcome.Transient, false),
            (null, HostFailure.Timeout, SourceOutcome.Transient, true),
            (null, HostFailure.Transport, SourceOutcome.Transient, false),
            (null, HostFailure.Cancelled, SourceOutcome.Cancelled, false),
            (200, HostFailure.Checksum, SourceOutcome.Persistent, false),
            (200, HostFailure.ForeignPdb, SourceOutcome.Persistent, false),
            (200, HostFailure.InvalidContent, SourceOutcome.Persistent, false),
            (302, HostFailure.Redirect, SourceOutcome.Persistent, false),
            (null, HostFailure.InsecureAddress, SourceOutcome.Persistent, false)
        };
        foreach (var item in cases)
            Assert.Equal(new HostClassification(item.Outcome, item.Skip) { Retry = item.Status is 429 or 503 && item.Failure == HostFailure.None },
                LibrarySourceDecisions.Classify(item.Status, item.Failure));
        // Cancellation stops the enumeration of later stages, outside the matrix of completed retrievals.
        Assert.Equal(LibraryCodeReason.Cancelled, LibrarySourceDecisions.Decide(true, false, [SourceOutcome.Cancelled], []).Reason);
        Assert.Equal(LibraryCodeReason.NoSources,
            LibrarySourceDecisions.Decide(true, true, [], [SourceOutcome.NoSourceAddress, SourceOutcome.Cancelled]).Reason);
        Assert.Equal(LibraryCodeReason.PersistentFailure,
            LibrarySourceDecisions.Decide(true, true, [], [SourceOutcome.Persistent, SourceOutcome.Cancelled]).Reason);
        Assert.Equal(LibraryCodeReason.Cancelled, LibrarySourceDecisions.Decide(true, true, [], [SourceOutcome.Cancelled]).Reason);
    }

    [Fact]
    public async Task Embedded_pdb_and_sources_preserve_compilation_options_without_requests()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address, embeddedPdb: true, embeddedDocuments: true);
        using var sources = Sources(library, server);
        var result = await sources.GetAsync(library.Dll);
        AssertSources(library, result);
        Assert.Equal(["SOURCE_TEST", "SECOND"], result.Defines);
        Assert.Equal(LanguageVersion.Preview, result.LanguageVersion);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Adjacent_pdb_accepts_exact_bytes_and_the_matching_crlf_form()
    {
        foreach (var crlf in new[] { false, true })
        {
            await using var server = new LoopbackSymbolServer();
            using var library = new SourceLibrary(server.Address, adjacent: true, crlf: crlf);
            library.Serve(server);
            foreach (var (path, bytes) in library.Documents)
                server.Replies["/sources/" + path.Split('/')[^1]] = new(Bytes: Encoding.UTF8.GetBytes(
                    Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal)));
            using var sources = Sources(library, server);
            AssertSources(library, await sources.GetAsync(library.Dll));
            Assert.All(server.Requests, request => Assert.StartsWith("/sources/", request.Path));
        }
    }

    [Fact]
    public async Task Ready_to_run_uses_the_portable_codeview_instead_of_the_first_entry()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address);
        library.AddWindowsCodeViewFirst();
        library.Serve(server);
        using var sources = Sources(library, server);
        AssertSources(library, await sources.GetAsync(library.Dll));
        Assert.Equal("/symbols/" + library.SymbolKey, server.Requests.First().Path);
    }

    [Fact]
    public async Task Nuget_is_queried_only_with_the_dll_checksum()
    {
        foreach (var checksum in new[] { true, false })
        {
            await using var server = new LoopbackSymbolServer();
            using var library = new SourceLibrary(server.Address, checksum: checksum);
            library.Serve(server, "nuget");
            using var sources = Sources(library, server);
            var result = await sources.GetAsync(library.Dll);
            if (checksum)
            {
                AssertSources(library, result);
                var request = Assert.Single(server.Requests, item => item.Path.StartsWith("/nuget/", StringComparison.Ordinal));
                using var pe = new PEReader(File.OpenRead(library.Dll));
                var sum = pe.ReadPdbChecksumDebugDirectoryData(pe.ReadDebugDirectory().Single(item => item.Type == DebugDirectoryEntryType.PdbChecksum));
                Assert.Equal(sum.AlgorithmName + ":" + Convert.ToHexString(sum.Checksum.AsSpan()), request.Headers["SymbolChecksum"]);
            }
            else
            {
                Assert.Equal(LibraryCodeReason.NoPdb, result.Reason);
                Assert.DoesNotContain(server.Requests, item => item.Path.StartsWith("/nuget/", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task Requests_carry_neither_authorization_nor_cookies_after_set_cookie_and_redirect()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address, adjacent: true);
        library.Serve(server);
        server.Replies["/sources/file0.cs"] = new(302, SetCookie: "session=secret; Path=/", Redirect: "/redirected");
        server.Replies["/redirected"] = new(Bytes: library.Documents["/src/file0.cs"], SetCookie: "second=secret; Path=/");
        using var handler = new System.Net.Http.SocketsHttpHandler { UseCookies = true, Credentials = System.Net.CredentialCache.DefaultCredentials };
        using var sources = Sources(library, server, handler: handler);
        AssertSources(library, await sources.GetAsync(library.Dll));
        Assert.Equal(3, server.Requests.Count);
        Assert.All(server.Requests, request =>
        {
            Assert.False(request.Headers.ContainsKey("Authorization"));
            Assert.False(request.Headers.ContainsKey("Cookie"));
            Assert.False(request.Headers.ContainsKey("Proxy-Authorization"));
        });
    }

    [Fact]
    public async Task Denied_and_timed_out_hosts_are_skipped_for_the_remaining_run()
    {
        var replies = new[]
        {
            new SymbolReply(401), new SymbolReply(403), new SymbolReply(407),
            new SymbolReply(Delay: Timeout.InfiniteTimeSpan), new SymbolReply(Bytes: [1], StallBody: true)
        };
        foreach (var reply in replies)
        {
            await using var server = new LoopbackSymbolServer();
            using var library = new SourceLibrary(server.Address, adjacent: true);
            library.Serve(server);
            server.Replies["/sources/file0.cs"] = reply;
            var clock = new FakeClock();
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.RequestReceived = _ => { if (reply.Delay != default) waiting.TrySetResult(); };
            server.BodyStalled = _ => waiting.TrySetResult();
            using var sources = new LibrarySources(false, library.Cache, new Uri(server.Address, "symbols/"),
                new Uri(server.Address, "nuget/"), httpsOnly: false, timeout: TimeSpan.FromMilliseconds(250)) { RequestTime = clock };
            var read = sources.GetAsync(library.Dll);
            if (reply.Delay != default || reply.StallBody)
            {
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.False(read.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(250));
            }
            var result = await read.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(reply.Status is 401 or 403 or 407 ? LibraryCodeKind.DecompiledBest : LibraryCodeKind.DecompiledForRun, result.Kind);
            Assert.Equal(reply.Status is 401 or 403 or 407 ? LibraryCodeReason.PersistentFailure : LibraryCodeReason.TemporaryFailure, result.Reason);
            Assert.Single(server.Requests);
            using var another = new SourceLibrary(server.Address, adjacent: true);
            var next = await sources.GetAsync(another.Dll).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(result.Kind, next.Kind);
            Assert.Equal(result.Reason, next.Reason);
            Assert.Equal(result.Detail, next.Detail);
            Assert.Single(server.Requests);
        }
    }

    [Fact]
    public async Task Https_redirect_to_http_is_rejected_before_sending_the_redirected_request()
    {
        await using var server = new LoopbackSymbolServer();
        var https = new UriBuilder(server.Address) { Scheme = "https" }.Uri;
        using var library = new SourceLibrary(https, adjacent: true, documentCount: 1);
        server.Replies["/sources/file0.cs"] = new(302, Redirect: new Uri(server.Address, "insecure").AbsoluteUri);
        using var sources = new LibrarySources(false, library.Cache, https, https, handler: server.HttpsHandler());
        var result = await sources.GetAsync(library.Dll);
        Assert.Equal(LibraryCodeReason.PersistentFailure, result.Reason);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Another_instance_reads_the_validated_pdb_and_documents_from_cache_offline()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address);
        library.Serve(server);
        using (var online = Sources(library, server))
            AssertSources(library, await online.GetAsync(library.Dll));
        server.Requests.Clear();
        using var offline = Sources(library, server, offline: true);
        AssertSources(library, await offline.GetAsync(library.Dll));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Failed_pdb_downloads_create_no_cache_directory()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address);
        using var sources = Sources(library, server);
        Assert.Equal(LibraryCodeReason.NoPdb, (await sources.GetAsync(library.Dll)).Reason);
        Assert.False(Directory.Exists(library.Cache));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Parallel_requests_for_one_dll_share_one_retrieval_per_document()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address);
        library.Serve(server);
        server.Replies["/symbols/" + library.SymbolKey] = new(Bytes: library.PdbBytes, Delay: TimeSpan.FromMilliseconds(50));
        using var sources = Sources(library, server);
        var results = await Task.WhenAll(sources.GetAsync(library.Dll), sources.GetAsync(library.Dll));
        Assert.Same(results[0], results[1]);
        AssertSources(library, results[0]);
        Assert.Equal(3, server.Requests.Count);
        Assert.All(server.Requests.GroupBy(item => item.Path), group => Assert.Single(group));
    }

    [Fact]
    public async Task Independent_writers_publish_only_complete_files_while_a_writer_is_cancelled()
    {
        foreach (var holdDocument in new[] { false, true })
        {
            await using var server = new LoopbackSymbolServer();
            using var library = new SourceLibrary(server.Address);
            library.Serve(server);
            var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            using var cancelled = new LibrarySources(false, library.Cache, new Uri(server.Address, "symbols/"),
                new Uri(server.Address, "nuget/"), httpsOnly: false)
            {
                CacheWriting = async (path, token) =>
                {
                    if (path.EndsWith(".source", StringComparison.Ordinal) != holdDocument) return;
                    writing.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            };
            var cancelledRead = cancelled.GetAsync(library.Dll, cancellation.Token);
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (var reader = Sources(library, server, offline: true))
            {
                var result = await reader.GetAsync(library.Dll);
                Assert.Equal(LibraryCodeKind.DecompiledForRun, result.Kind);
                Assert.Empty(result.Documents);
            }
            using var first = Sources(library, server);
            using var second = Sources(library, server);
            var successful = await Task.WhenAll(first.GetAsync(library.Dll), second.GetAsync(library.Dll));
            foreach (var result in successful) AssertSources(library, result);
            await cancellation.CancelAsync();
            Assert.Equal(LibraryCodeReason.Cancelled, (await cancelledRead).Reason);
            Assert.Empty(Directory.GetFiles(library.Cache, "*.tmp", SearchOption.AllDirectories));
            using var final = Sources(library, server, offline: true);
            AssertSources(library, await final.GetAsync(library.Dll));
        }
    }

    [Fact]
    public async Task Foreign_and_checksum_mismatched_pdbs_are_rejected_then_the_next_server_is_used()
    {
        foreach (var foreign in new[] { false, true })
        {
            await using var server = new LoopbackSymbolServer();
            using var library = new SourceLibrary(server.Address);
            using var other = new SourceLibrary(server.Address, crlf: true);
            library.Serve(server, "nuget");
            var bad = foreign ? other.PdbBytes : (byte[])library.PdbBytes.Clone();
            if (!foreign) bad[^1] ^= 1;
            server.Replies["/symbols/" + library.SymbolKey] = new(Bytes: bad);
            File.WriteAllBytes(library.Pdb, bad);
            using var sources = Sources(library, server);
            AssertSources(library, await sources.GetAsync(library.Dll));
            Assert.Equal(4, server.Requests.Count);
        }
    }

    [Fact]
    public async Task Optional_documents_can_be_absent_but_required_documents_use_stable_reason_priority()
    {
        await using var server = new LoopbackSymbolServer();
        using (var optional = new SourceLibrary(server.Address, adjacent: true, optionalDocument: true))
        {
            optional.Serve(server);
            server.Replies.TryRemove("/sources/AssemblyInfo.cs", out _);
            using var sources = Sources(optional, server);
            var result = await sources.GetAsync(optional.Dll);
            Assert.Equal(LibraryCodeKind.OriginalSources, result.Kind);
            Assert.Equal(2, result.Documents.Count);
        }
        using (var missing = new SourceLibrary(server.Address, adjacent: true, sourceLink: false))
        {
            using var sources = Sources(missing, server, offline: true);
            Assert.Equal(LibraryCodeReason.NoSources, (await sources.GetAsync(missing.Dll)).Reason);
        }
        foreach (var status in new[] { 404, 410, 200, 500, 429 })
        {
            using var library = new SourceLibrary(server.Address, adjacent: true);
            library.Serve(server);
            server.Replies["/sources/file0.cs"] = new(status, Bytes: Encoding.UTF8.GetBytes("bad checksum"));
            using var sources = new LibrarySources(false, library.Cache, httpsOnly: false, timeout: Timeout.InfiniteTimeSpan)
            { RequestTime = new SourceRetryClock() };
            var result = await sources.GetAsync(library.Dll);
            Assert.Equal(status is 500 or 429 ? LibraryCodeKind.DecompiledForRun : LibraryCodeKind.DecompiledBest, result.Kind);
        }
        // A later cancellation must not erase a stable outcome already observed for a required document.
        using var mixed = new SourceLibrary(server.Address, adjacent: true);
        mixed.Serve(server);
        server.Replies["/sources/file0.cs"] = new(404);
        server.Replies["/sources/file1.cs"] = new(Delay: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        server.RequestReceived = request => { if (request.Path == "/sources/file1.cs") cancellation.Cancel(); };
        using var mixedSources = Sources(mixed, server);
        Assert.Equal(LibraryCodeReason.PersistentFailure, (await mixedSources.GetAsync(mixed.Dll, cancellation.Token)).Reason);
    }

    [Fact]
    public async Task Offline_missing_sources_and_cancellation_are_for_this_run_and_send_no_requests()
    {
        await using var server = new LoopbackSymbolServer();
        foreach (var adjacent in new[] { false, true })
        {
            using var library = new SourceLibrary(server.Address, adjacent: adjacent);
            using var offline = Sources(library, server, offline: true);
            var result = await offline.GetAsync(library.Dll);
            Assert.Equal(LibraryCodeKind.DecompiledForRun, result.Kind);
            Assert.Equal(LibraryCodeReason.Offline, result.Reason);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            using var cancelled = Sources(library, server);
            Assert.Equal(LibraryCodeReason.Cancelled, (await cancelled.GetAsync(library.Dll, cancellation.Token)).Reason);
        }
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task No_portable_codeview_is_best_code_and_http_is_allowed_only_on_loopback_in_tests()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address);
        library.RemovePortableCodeView();
        using (var sources = Sources(library, server, offline: true))
        {
            var result = await sources.GetAsync(library.Dll);
            Assert.Equal(LibraryCodeKind.DecompiledBest, result.Kind);
            Assert.Equal(LibraryCodeReason.NoPdb, result.Reason);
        }
        using var insecure = new SourceLibrary(new Uri("http://example.invalid/"), adjacent: true);
        using (var sources = Sources(insecure, server))
            Assert.Equal(LibraryCodeReason.PersistentFailure, (await sources.GetAsync(insecure.Dll)).Reason);
        using var strict = new SourceLibrary(server.Address, adjacent: true);
        using (var sources = new LibrarySources(false, strict.Cache))
            Assert.Equal(LibraryCodeReason.PersistentFailure, (await sources.GetAsync(strict.Dll)).Reason);
        Assert.Empty(server.Requests);
    }

    private static LibrarySources Sources(SourceLibrary library, LoopbackSymbolServer server, bool offline = false,
                                          System.Net.Http.HttpMessageHandler? handler = null) =>
        new(offline, library.Cache, new Uri(server.Address, "symbols/"), new Uri(server.Address, "nuget/"),
            httpsOnly: false, handler: handler);

    private static void AssertSources(SourceLibrary library, LibrarySourceResult result)
    {
        Assert.Equal(LibraryCodeKind.OriginalSources, result.Kind);
        Assert.Null(result.Reason);
        Assert.NotNull(result.PdbId);
        Assert.Equal(library.Documents.Count, result.Documents.Count);
        foreach (var document in result.Documents)
        {
            Assert.Equal(library.Documents[document.Path], document.Content.ToArray());
            Assert.Equal(Encoding.UTF8.GetString(library.Documents[document.Path]), document.Text.ToString());
        }
    }
}
