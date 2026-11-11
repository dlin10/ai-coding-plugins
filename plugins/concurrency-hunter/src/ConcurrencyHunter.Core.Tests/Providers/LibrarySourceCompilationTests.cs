using System.Text;
using System.Text.Json;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class LibrarySourceCompilationTests
{
    private const string SOURCE = "public static class Api { public static void Run(System.Action action) => action(); }";
    private const string MEMBER = "M:Api.Run(System.Action)";

    [Fact]
    public async Task Pdb_defines_and_language_version_parse_the_original_documents()
    {
        const string source = """"
            #if SOURCE_TEST
            public static class Api { public static string Text() => """original"""; }
            #else
            #error Missing PDB defines
            #endif
            """";
        using var fixture = new Fixture(source, language: LanguageVersion.CSharp11, defines: ["SOURCE_TEST"]);
        using var sources = new LibrarySources(true, Path.Combine(fixture.Install.Root, "cache"));
        var prepared = await sources.GetAsync(fixture.Assembly.Path);
        Assert.Equal(LibraryCodeKind.OriginalSources, prepared.Kind);
        Assert.Equal(LanguageVersion.CSharp11, prepared.LanguageVersion);
        Assert.Contains("SOURCE_TEST", prepared.Defines);
        var decompiledTypes = 0;
        var result = fixture.Compile(prepared, new LibraryCompilationProbe
        {
            TypeStarting = _ => Interlocked.Increment(ref decompiledTypes)
        });
        Assert.Equal(0, decompiledTypes);
        Assert.Null(result.Reason);
        Assert.Equal(0, result.ExternBodies);
        var tree = Assert.Single(result.Compilation!.SyntaxTrees, tree => tree.FilePath == "/src/library.cs");
        Assert.Equal(LanguageVersion.CSharp11, ((CSharpParseOptions)tree.Options).LanguageVersion);
        Assert.Contains("\"\"\"original\"\"\"", tree.ToString(), StringComparison.Ordinal);
        Assert.Equal(NullableContextOptions.Disable, result.Compilation.Options.NullableContextOptions);
        var document = prepared.Documents.Single();
        var changed = prepared with { Documents = [document with { Content = [.. Encoding.UTF8.GetBytes(source.Replace("original", "changed", StringComparison.Ordinal))] }] };
        var changedCopy = fixture.Compile(changed);
        Assert.NotSame(result, changedCopy);
        Assert.Contains(changedCopy.Compilation!.SyntaxTrees, candidate => candidate.FilePath == document.Path && candidate.ToString().Contains("changed", StringComparison.Ordinal));
        var movedCopy = fixture.Compile(prepared with { Documents = [document with { Path = "/src/moved.cs" }] });
        Assert.NotSame(result, movedCopy);
        Assert.Contains(movedCopy.Compilation!.SyntaxTrees, candidate => candidate.FilePath == "/src/moved.cs");
        // The same text is not accepted under a language version predating raw string literals.
        var rejected = fixture.Compile(prepared with { LanguageVersion = LanguageVersion.CSharp10, PdbId = "older-language" });
        Assert.Null(rejected.Reason);
        Assert.True(rejected.ExternBodies > 0);
    }

    [Fact]
    public async Task Missing_methodless_document_restores_identity_public_key_and_friends()
    {
        var key = Convert.ToHexString(EmittedAssemblies.TestKey);
        var attributes = $"""
            [assembly: System.Reflection.AssemblyVersion("3.2.1.0")]
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Friend, PublicKey={key}")]
            """;
        using var fixture = new Fixture(SOURCE, attributes, signed: true);
        using var sources = new LibrarySources(true, Path.Combine(fixture.Install.Root, "cache"));
        var prepared = await sources.GetAsync(fixture.Assembly.Path);
        Assert.Equal(LibraryCodeKind.OriginalSources, prepared.Kind);
        Assert.DoesNotContain(prepared.Documents, document => document.Path == "/src/AssemblyInfo.cs");
        var copy = fixture.Compile(prepared).Compilation!;
        Assert.Equal("Fixture.Sources", copy.Assembly.Identity.Name);
        Assert.Equal(new Version(3, 2, 1, 0), copy.Assembly.Identity.Version);
        Assert.Equal(EmittedAssemblies.TestKey, copy.Assembly.Identity.PublicKey.ToArray());
        Assert.True(copy.Options.PublicSign);
        Assert.Contains(copy.Assembly.GetAttributes(), attribute =>
            attribute.AttributeClass?.Name == "InternalsVisibleToAttribute" &&
            (string?)attribute.ConstructorArguments[0].Value == $"Friend, PublicKey={key}");
    }

    [Fact]
    public void Existing_single_attributes_are_matched_by_type_through_aliases()
    {
        const string source = """
            using Title = System.Reflection.AssemblyTitleAttribute;
            [assembly: Title("source title")]
            public static class Api { public static void Run(System.Action action) => action(); }
            """;
        using var fixture = new Fixture(source);
        var copy = fixture.Compile(fixture.Prepared(source.Replace("source title", "prepared title", StringComparison.Ordinal))).Compilation!;
        var title = Assert.Single(copy.Assembly.GetAttributes(), attribute => attribute.AttributeClass?.Name == "AssemblyTitleAttribute");
        Assert.Equal("prepared title", title.ConstructorArguments[0].Value);
        Assert.DoesNotContain(copy.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0579");
    }

    [Fact]
    public void AllowMultiple_compares_typed_constants_arrays_types_and_named_arguments()
    {
        const string attributes = """
            [assembly: Mark(new[] { 1, 2 }, typeof(string), Flag = true)]
            [assembly: Mark(new[] { 3 }, typeof(int), Flag = false)]
            """;
        const string source = """
            [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
            public sealed class MarkAttribute : System.Attribute
            {
                public MarkAttribute(int[] values, System.Type type) { }
                public bool Flag { get; set; }
            }
            public static class Api { public static void Run(System.Action action) => action(); }
            """;
        using var fixture = new Fixture(attributes + source);
        var prepared = fixture.Prepared("[assembly: Mark(new int[] { 1, 2 }, typeof(System.String), Flag = true)]\n" + source);
        var result = fixture.Compile(prepared);
        Assert.Null(result.Reason);
        var marks = result.Compilation!.Assembly.GetAttributes().Where(attribute => attribute.AttributeClass?.Name == "MarkAttribute").ToArray();
        Assert.Equal(2, marks.Length);
        Assert.Single(marks, attribute => attribute.ConstructorArguments[0].Values.Length == 2);
        Assert.Single(marks, attribute => attribute.ConstructorArguments[0].Values.Length == 1);
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Refused_sources_fall_back_and_both_entries_are_released_with_the_run()
    {
        using var fixture = new Fixture(SOURCE);
        var prepared = fixture.Rejected();
        var started = 0;
        var rejected = 0;
        var probe = new LibraryCompilationProbe { Started = () => Interlocked.Increment(ref started) };
        LibraryCompilationResult first;
        using (fixture.Ownership.Open())
            first = LibraryCompilation.Compile(fixture.Assembly, prepared, new GenerationChecks(CancellationToken.None), () => rejected++, probe);
        Assert.Null(first.Reason);
        Assert.Equal(1, rejected);
        Assert.Equal(2, started);
        Assert.Equal(2, fixture.Ownership.Created.Count);
        Assert.Contains(fixture.Ownership.Created, entry => entry.Key.Contains("code:sources:", StringComparison.Ordinal));
        Assert.Contains(fixture.Ownership.Created, entry => entry.Key.EndsWith("code:decompiled", StringComparison.Ordinal));
        LibraryCompilation.Release(fixture.Ownership.Created);
        var second = fixture.Compile(prepared, probe);
        Assert.NotSame(first, second);
        Assert.Equal(4, started);
        Assert.DoesNotContain(second.Compilation!.SyntaxTrees, tree => tree.FilePath == "/src/library.cs");
    }

    [Fact]
    public void Sequential_callers_keep_their_own_code_in_both_orders()
    {
        foreach (var reverse in new[] { false, true })
        {
            using var fixture = new Fixture(SOURCE);
            var callers = fixture.Callers();
            foreach (var (prepared, expected) in reverse ? callers.Reverse() : callers)
            {
                var answer = fixture.Generate(prepared);
                Assert.NotNull(answer.Model);
                Assert.Equal(expected, answer.Generation.Code);
                AssertCode(answer, expected);
            }
            // A best-code caller and a run-only caller use exactly the same decompiled compilation.
            Assert.Same(fixture.Compile(callers[0].Prepared), fixture.Compile(callers[2].Prepared));
        }
    }

    [Fact]
    public async Task Concurrent_callers_keep_their_own_code_in_both_orders()
    {
        foreach (var reverse in new[] { false, true })
        {
            using var fixture = new Fixture(SOURCE);
            var callers = fixture.Callers();
            using var ready = new CountdownEvent(callers.Length);
            using var start = new ManualResetEventSlim();
            var tasks = (reverse ? callers.Reverse() : callers).Select(caller => Task.Run(() =>
            {
                ready.Signal();
                Assert.True(start.Wait(TimeSpan.FromSeconds(30)));
                var answer = fixture.Generate(caller.Prepared);
                Assert.NotNull(answer.Model);
                Assert.Equal(caller.Expected, answer.Generation.Code);
                AssertCode(answer, caller.Expected);
            })).ToArray();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
            start.Set();
            await Task.WhenAll(tasks);
        }
    }

    [Fact]
    public void Timeout_before_compilation_writes_null_code()
    {
        using var fixture = new Fixture(SOURCE);
        var clock = new FakeClock();
        var answer = ModelGenerator.TraceWithin(fixture.Request, fixture.Install.Resolver(), TimeSpan.FromSeconds(1), clock,
            checkReported: (stage, _) => { if (stage == GenerationStage.Resolve) clock.Advance(TimeSpan.FromSeconds(2)); }).Answer;
        Assert.Equal(GenerationReasons.TIMEOUT, answer.Reason);
        Assert.Null(answer.Generation.Code);
        using var document = JsonDocument.Parse(GeneratedDocument.Write(answer));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("generation").GetProperty("code").ValueKind);
    }

    [Fact]
    public void Timeout_during_original_compilation_keeps_sources_code()
    {
        using var fixture = new Fixture(SOURCE);
        AssertCode(fixture.Timeout(fixture.Prepared()), new(LibraryCodeKind.OriginalSources, null));
    }

    [Fact]
    public void Timeout_during_default_decompilation_keeps_sources_not_sought()
    {
        using var fixture = new Fixture(SOURCE);
        AssertCode(fixture.Timeout(null), new(LibraryCodeKind.DecompiledBest, LibraryCodeReason.SourcesNotSought));
    }

    [Fact]
    public void Timeout_during_run_only_decompilation_keeps_its_own_reason()
    {
        using var fixture = new Fixture(SOURCE);
        var prepared = fixture.Prepared() with { Kind = LibraryCodeKind.DecompiledForRun, Reason = LibraryCodeReason.Offline };
        AssertCode(fixture.Timeout(prepared), new(LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Offline));
    }

    [Fact]
    public void Timeout_during_fallback_already_reports_sources_rejected()
    {
        foreach (var outcome in new[] { SourceOutcome.Persistent, SourceOutcome.Transient, SourceOutcome.UnavailableOffline })
        {
            using var fixture = new Fixture(SOURCE);
            var prepared = fixture.Rejected() with { MissingMethodlessDocuments = [outcome] };
            var expected = outcome == SourceOutcome.Transient ? new LibraryCode(LibraryCodeKind.DecompiledForRun, LibraryCodeReason.TemporaryFailure)
                : outcome == SourceOutcome.UnavailableOffline ? new(LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Offline)
                : new(LibraryCodeKind.DecompiledBest, LibraryCodeReason.SourcesRejected);
            AssertCode(fixture.Timeout(prepared, compilation: 2), expected);
        }
    }

    [Fact]
    public async Task Document_sets_and_methodless_failures_stay_with_each_sequential_caller()
    {
        await CheckDocumentSets(concurrent: false);
    }

    [Fact]
    public async Task Document_sets_and_methodless_failures_stay_with_each_concurrent_caller()
    {
        await CheckDocumentSets(concurrent: true);
    }

    private static async Task CheckDocumentSets(bool concurrent)
    {
        foreach (var refused in new[] { false, true })
        foreach (var failure in new[] { SourceOutcome.Persistent, SourceOutcome.NoSourceAddress, SourceOutcome.Transient,
                                        SourceOutcome.UnavailableOffline })
        foreach (var mixed in new[] { false, true })
        foreach (var reverse in new[] { false, true })
        {
            await using var server = new LoopbackSymbolServer();
            var source = SOURCE.Replace("static class Api", refused ? "class Api : IMarker" : "class Api", StringComparison.Ordinal);
            const string optional = "public interface IMarker { }";
            const string extra = "public interface IExtra { }";
            using var fixture = new Fixture(source, optional, sourceServer: server.Address, extraDocument: mixed ? extra : null,
                linkMethodless: failure != SourceOutcome.NoSourceAddress);
            server.Replies["/sources/AssemblyInfo.cs"] = new(Bytes: Encoding.UTF8.GetBytes(optional));
            server.Replies["/sources/Extra.cs"] = new(Bytes: Encoding.UTF8.GetBytes(extra));
            using var fullSources = new LibrarySources(false, Path.Combine(fixture.Install.Root, "full"), httpsOnly: false);
            var full = await fullSources.GetAsync(fixture.Assembly.Path);
            if (failure == SourceOutcome.NoSourceAddress)
                full = full with { Documents = full.Documents.Append(new("/src/AssemblyInfo.cs", [.. Encoding.UTF8.GetBytes(optional)]))
                    .Concat(mixed ? [new LibrarySourceDocument("/src/Extra.cs", [.. Encoding.UTF8.GetBytes(extra)])] : []).ToArray() };
            Assert.Equal(mixed ? 3 : 2, full.Documents.Count);
            server.Replies["/sources/AssemblyInfo.cs"] = new(failure == SourceOutcome.Transient ? 503 : 404);
            server.Replies["/sources/Extra.cs"] = new(404);
            using var missingSources = new LibrarySources(failure == SourceOutcome.UnavailableOffline,
                Path.Combine(fixture.Install.Root, "missing"), httpsOnly: false);
            var missing = await missingSources.GetAsync(fixture.Assembly.Path);
            Assert.Single(missing.Documents);
            Assert.Equal(full.PdbId, missing.PdbId);
            var expected = !refused ? new LibraryCode(LibraryCodeKind.OriginalSources, null)
                : failure == SourceOutcome.Transient ? new(LibraryCodeKind.DecompiledForRun, LibraryCodeReason.TemporaryFailure)
                : failure == SourceOutcome.UnavailableOffline ? new(LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Offline)
                : new(LibraryCodeKind.DecompiledBest, LibraryCodeReason.SourcesRejected);
            if (refused && failure == SourceOutcome.Transient)
                expected = expected with { Detail = new Uri(server.Address, "sources/AssemblyInfo.cs").AbsoluteUri + " (host " + server.Address.Host + "): HTTP 503" };
            var callers = new[] { (Prepared: full, Expected: new LibraryCode(LibraryCodeKind.OriginalSources, null)), (Prepared: missing, Expected: expected) };
            if (reverse)
                Array.Reverse(callers);
            if (concurrent)
            {
                using var ready = new CountdownEvent(callers.Length);
                using var start = new ManualResetEventSlim();
                var tasks = callers.Select(caller => Task.Run(() =>
                {
                    ready.Signal();
                    Assert.True(start.Wait(TimeSpan.FromSeconds(30)));
                    AssertCode(fixture.Generate(caller.Prepared), caller.Expected);
                })).ToArray();
                Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
                start.Set();
                await Task.WhenAll(tasks);
            }
            else
                foreach (var caller in callers)
                    AssertCode(fixture.Generate(caller.Prepared), caller.Expected);
            var compiled = fixture.Compile(full);
            Assert.Contains(compiled.Compilation!.SyntaxTrees, tree => tree.FilePath == "/src/AssemblyInfo.cs");
            var incomplete = fixture.Compile(missing);
            Assert.DoesNotContain(incomplete.Compilation!.SyntaxTrees, tree => tree.FilePath == "/src/AssemblyInfo.cs");
            Assert.Equal(!refused, incomplete.Compilation.SyntaxTrees.Any(tree => tree.FilePath == "/src/library.cs"));
        }
    }

    private static void AssertCode(GeneratedAnswer answer, LibraryCode expected)
    {
        Assert.Equal(expected, answer.Generation.Code);
        using var document = JsonDocument.Parse(GeneratedDocument.Write(answer));
        Assert.Equal(5, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var code = document.RootElement.GetProperty("generation").GetProperty("code");
        var kind = expected.Kind switch
        {
            LibraryCodeKind.OriginalSources => "sources",
            LibraryCodeKind.DecompiledForRun => "decompiled-this-run",
            _ => "decompiled"
        };
        Assert.Equal(kind, code.GetProperty("kind").GetString());
        var reason = expected.Reason switch
        {
            LibraryCodeReason.NoPdb => "no-pdb",
            LibraryCodeReason.TemporaryFailure => "temporary-failure",
            LibraryCodeReason.Offline => "offline",
            LibraryCodeReason.SourcesRejected => "sources-rejected",
            LibraryCodeReason.SourcesNotSought => "sources-not-sought",
            _ => null
        };
        Assert.Equal(reason, code.GetProperty("reason").GetString());
        Assert.Equal(expected.Detail, code.GetProperty("detail").GetString());
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _source;
        private readonly LanguageVersion _language;
        private readonly string[] _defines;
        internal GenerationInstall Install { get; } = new();
        internal CompilationOwnership Ownership { get; } = new();
        internal ImplementationAssembly Assembly { get; }
        internal GenerationRequest Request { get; } = new("Fixture.Sources", "8.0", MEMBER, null, null);

        internal Fixture(string source, string? attributes = null, bool signed = false,
                         LanguageVersion language = LanguageVersion.Preview, string[]? defines = null,
                         Uri? sourceServer = null, string? extraDocument = null, bool linkMethodless = true)
        {
            _source = source;
            _language = language;
            _defines = defines ?? [];
            var parse = new CSharpParseOptions(language, preprocessorSymbols: _defines);
            var text = SourceText.From(source, Encoding.UTF8, SourceHashAlgorithm.Sha256);
            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(text, parse, "/src/library.cs") };
            if (attributes is not null)
                trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(attributes, new UTF8Encoding(false)), parse, "/src/AssemblyInfo.cs"));
            if (extraDocument is not null)
                trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(extraDocument, new UTF8Encoding(false)), parse, "/src/Extra.cs"));
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            if (signed)
                options = options.WithCryptoPublicKey([.. EmittedAssemblies.TestKey]).WithPublicSign(true);
            var compilation = CSharpCompilation.Create("Fixture.Sources", trees, EmittedAssemblies.RuntimeReferences, options);
            using var image = new MemoryStream();
            using var pdb = new MemoryStream();
            using var links = sourceServer is null ? null : new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                documents = linkMethodless ? new Dictionary<string, string> { ["/src/*"] = new Uri(sourceServer, "sources/*").AbsoluteUri }
                    : new Dictionary<string, string> { ["/src/library.cs"] = new Uri(sourceServer, "sources/library.cs").AbsoluteUri }
            }));
            var emitted = compilation.Emit(image, pdbStream: pdb,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: "Fixture.Sources.pdb",
                                         pdbChecksumAlgorithm: HashAlgorithmName.SHA256),
                sourceLinkStream: links,
                embeddedTexts: sourceServer is null ? [EmbeddedText.FromSource("/src/library.cs", text)]
                    : trees.Select(tree => EmbeddedText.FromSource(tree.FilePath, tree.GetText())));
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            var imageBytes = image.ToArray();
            var pdbBytes = pdb.ToArray();
            if (sourceServer is not null)
            {
                // Embedding registers even methodless documents. A mismatched embedded payload then forces Source Link/cache lookup.
                foreach (var document in new[] { attributes, extraDocument }.OfType<string>())
                {
                    var offset = pdbBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(document));
                    Assert.True(offset >= 0);
                    pdbBytes[offset] ^= 1;
                }
                using var reader = new PEReader(new MemoryStream(imageBytes));
                var checksum = reader.ReadDebugDirectory().Single(entry => entry.Type == DebugDirectoryEntryType.PdbChecksum);
                using var provider = MetadataReaderProvider.FromPortablePdbImage([.. pdbBytes]);
                var header = provider.GetMetadataReader().DebugMetadataHeader!;
                var checksumBytes = (byte[])pdbBytes.Clone();
                checksumBytes.AsSpan(header.IdStartOffset, header.Id.Length).Clear();
                SHA256.HashData(checksumBytes).CopyTo(imageBytes.AsSpan(checksum.DataPointer + "SHA256\0".Length));
            }
            var directory = Install.SharedFramework("8.0.5", runtime: true, images: new Dictionary<string, byte[]> { ["Fixture.Sources"] = imageBytes });
            File.WriteAllBytes(Path.Combine(directory, "Fixture.Sources.pdb"), pdbBytes);
            Assembly = Install.Resolver().Resolve(Request.AssemblyName, Request.Version, null, null).Assembly!;
            Assert.NotNull(Assembly);
        }

        internal LibrarySourceResult Prepared(string? source = null) => new(LibraryCodeKind.OriginalSources, null,
            [new("/src/library.cs", [.. Encoding.UTF8.GetBytes(source ?? _source)])], _defines, _language, "fixture-pdb");

        internal LibrarySourceResult Rejected() => Prepared("public class Broken : MissingType { }") with { PdbId = "refused-pdb" };

        internal (LibrarySourceResult? Prepared, LibraryCode Expected)[] Callers() =>
        [
            (null, new(LibraryCodeKind.DecompiledBest, LibraryCodeReason.SourcesNotSought)),
            (Prepared() with { Kind = LibraryCodeKind.DecompiledBest, Reason = LibraryCodeReason.NoPdb },
                new(LibraryCodeKind.DecompiledBest, LibraryCodeReason.NoPdb)),
            (Prepared() with { Kind = LibraryCodeKind.DecompiledForRun, Reason = LibraryCodeReason.TemporaryFailure },
                new(LibraryCodeKind.DecompiledForRun, LibraryCodeReason.TemporaryFailure)),
            (Rejected(), new(LibraryCodeKind.DecompiledBest, LibraryCodeReason.SourcesRejected)),
            (Prepared(), new(LibraryCodeKind.OriginalSources, null))
        ];

        internal LibraryCompilationResult Compile(LibrarySourceResult? prepared, LibraryCompilationProbe? probe = null)
        {
            using var ownership = Ownership.Open();
            return LibraryCompilation.Compile(Assembly, prepared, new GenerationChecks(CancellationToken.None), () => { }, probe);
        }

        internal GeneratedAnswer Generate(LibrarySourceResult? prepared)
        {
            using var ownership = Ownership.Open();
            return ModelGenerator.TraceWithin(Request, Install.Resolver(), TimeSpan.FromMinutes(1), sources: prepared).Answer;
        }

        internal GeneratedAnswer Timeout(LibrarySourceResult? prepared, int compilation = 1)
        {
            using var ownership = Ownership.Open();
            var clock = new FakeClock();
            var started = 0;
            var probe = new LibraryCompilationProbe
            {
                Started = () => { if (Interlocked.Increment(ref started) == compilation) clock.Advance(TimeSpan.FromSeconds(2)); }
            };
            var answer = ModelGenerator.TraceWithin(Request, Install.Resolver(), TimeSpan.FromSeconds(1), clock,
                                                   sources: prepared, compilationProbe: probe).Answer;
            Assert.Equal(GenerationReasons.TIMEOUT, answer.Reason);
            return answer;
        }

        public void Dispose()
        {
            try
            {
                foreach (var entry in Ownership.Created)
                {
                    try { entry.Entry.Value.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                LibraryCompilation.Release(Ownership.Created);
                Install.Dispose();
            }
        }
    }
}
