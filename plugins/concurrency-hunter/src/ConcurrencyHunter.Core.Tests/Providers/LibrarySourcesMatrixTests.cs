using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class LibrarySourcesMatrixTests
{
    private enum LocalPdb { Embedded, Beside, Cached, ForeignBeside, Absent }
    private enum Server
    {
        NotAsked, Pdb, NotFound, Gone, ForeignPdb, Unauthorized, Forbidden, ProxyAuthentication,
        Transient, SkippedTransient, SkippedUnauthorized, SkippedForbidden, SkippedProxyAuthentication
    }
    private enum Document
    {
        NotRead, Embedded, Cached, NoSourceAddress, Received, ConvertedCrlf, Persistent, Transient,
        SkippedTransient, SkippedDenied
    }
    private enum OptionalDocument { NotRead, Present, StableAbsence, Transient, UnavailableOffline }
    private enum Compilation { NotReached, Compiled, Refused }
    private sealed record Cell(bool Offline, bool Portable, bool Checksum, LocalPdb Local, Server Microsoft, Server Nuget,
                               Document First, Document Second, OptionalDocument OptionalFirst, OptionalDocument OptionalSecond,
                               Compilation Compilation);

    private static readonly Cell[] Cells = Enumerate().ToArray();

    private static IEnumerable<Cell> Enumerate()
    {
        foreach (var offline in new[] { false, true })
        foreach (var portable in new[] { false, true })
        foreach (var checksum in new[] { false, true })
        foreach (var local in Enum.GetValues<LocalPdb>())
        foreach (var microsoft in Servers(portable && !offline && !LocalFound(local)))
        foreach (var nuget in Servers(portable && !offline && !LocalFound(local) && checksum && microsoft != Server.Pdb))
        {
            var found = portable && (LocalFound(local) || microsoft == Server.Pdb || nuget == Server.Pdb);
            if (!found)
            {
                yield return new(offline, portable, checksum, local, microsoft, nuget,
                    Document.NotRead, Document.NotRead, OptionalDocument.NotRead, OptionalDocument.NotRead, Compilation.NotReached);
                continue;
            }
            foreach (var first in Enum.GetValues<Document>().Where(value => value != Document.NotRead))
            foreach (var second in Enum.GetValues<Document>().Where(value => value != Document.NotRead))
            foreach (var optionalFirst in OptionalDocuments(offline))
            foreach (var optionalSecond in OptionalDocuments(offline))
            foreach (var compilation in CanCompile(first, offline) && CanCompile(second, offline)
                ? new[] { Compilation.Compiled, Compilation.Refused } : [Compilation.NotReached])
                yield return new(offline, portable, checksum, local, microsoft, nuget, first, second, optionalFirst, optionalSecond, compilation);
        }
    }

    private static IEnumerable<Server> Servers(bool asked) => asked
        ? Enum.GetValues<Server>().Where(value => value != Server.NotAsked) : [Server.NotAsked];

    private static OptionalDocument[] OptionalDocuments(bool offline) => offline
        ? [OptionalDocument.Present, OptionalDocument.StableAbsence, OptionalDocument.UnavailableOffline]
        : [OptionalDocument.Present, OptionalDocument.StableAbsence, OptionalDocument.Transient];

    private static bool LocalFound(LocalPdb local) => local is LocalPdb.Embedded or LocalPdb.Beside or LocalPdb.Cached;
    private static bool Temporary(Server server) => server is Server.Transient or Server.SkippedTransient;
    private static bool LocalDocument(Document document) => document is Document.Embedded or Document.Cached;
    private static bool StableDocument(Document document) => document is Document.Persistent or Document.SkippedDenied;
    private static bool TemporaryDocument(Document document) => document is Document.Transient or Document.SkippedTransient;
    private static bool CanCompile(Document document, bool offline) => LocalDocument(document) ||
        !offline && document is Document.Received or Document.ConvertedCrlf;

    // Exact expectations only: every reachable cell has its own entry; no wider-cell fallback is used.
    private static (LibraryCodeKind Kind, LibraryCodeReason? Reason) Expected(Cell cell)
    {
        if (!cell.Portable)
            return (LibraryCodeKind.DecompiledBest, LibraryCodeReason.NoPdb);
        if (!LocalFound(cell.Local) && cell.Microsoft != Server.Pdb && cell.Nuget != Server.Pdb)
            return cell.Offline ? (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Offline)
                : Temporary(cell.Microsoft) || Temporary(cell.Nuget)
                    ? (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.TemporaryFailure)
                    : (LibraryCodeKind.DecompiledBest, LibraryCodeReason.NoPdb);
        if (cell.First == Document.NoSourceAddress || cell.Second == Document.NoSourceAddress)
            return (LibraryCodeKind.DecompiledBest, LibraryCodeReason.NoSources);
        if (cell.Offline)
        {
            if (!LocalDocument(cell.First) || !LocalDocument(cell.Second))
                return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Offline);
        }
        else
        {
            if (StableDocument(cell.First) || StableDocument(cell.Second))
                return (LibraryCodeKind.DecompiledBest, LibraryCodeReason.PersistentFailure);
            if (TemporaryDocument(cell.First) || TemporaryDocument(cell.Second))
                return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.TemporaryFailure);
        }
        if (cell.Compilation == Compilation.Refused)
        {
            if (cell.OptionalFirst == OptionalDocument.UnavailableOffline || cell.OptionalSecond == OptionalDocument.UnavailableOffline)
                return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Offline);
            if (cell.OptionalFirst == OptionalDocument.Transient || cell.OptionalSecond == OptionalDocument.Transient)
                return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.TemporaryFailure);
            return (LibraryCodeKind.DecompiledBest, LibraryCodeReason.SourcesRejected);
        }
        return (LibraryCodeKind.OriginalSources, null);
    }

    private static SourceOutcome ServerOutcome(Server server) => server switch
    {
        Server.Pdb => SourceOutcome.Received,
        Server.Transient or Server.SkippedTransient => SourceOutcome.Transient,
        Server.NotFound or Server.Gone or Server.ForeignPdb or Server.Unauthorized or Server.Forbidden or Server.ProxyAuthentication or
            Server.SkippedUnauthorized or Server.SkippedForbidden or Server.SkippedProxyAuthentication => SourceOutcome.Persistent,
        _ => throw new InvalidOperationException("An unasked server has no outcome.")
    };

    private static SourceOutcome DocumentOutcome(Document document, bool offline) => document switch
    {
        Document.Embedded or Document.Cached => SourceOutcome.Received,
        Document.NoSourceAddress => SourceOutcome.NoSourceAddress,
        Document.NotRead => throw new InvalidOperationException("An unread document has no outcome."),
        _ when offline => SourceOutcome.UnavailableOffline,
        Document.Received or Document.ConvertedCrlf => SourceOutcome.Received,
        Document.Persistent or Document.SkippedDenied => SourceOutcome.Persistent,
        Document.Transient or Document.SkippedTransient => SourceOutcome.Transient,
        _ => throw new InvalidOperationException(document.ToString())
    };

    private static (LibraryCodeKind Kind, LibraryCodeReason? Reason) Decide(Cell cell)
    {
        var found = cell.Portable && (LocalFound(cell.Local) || cell.Microsoft == Server.Pdb || cell.Nuget == Server.Pdb);
        var servers = new[] { cell.Microsoft, cell.Nuget }.Where(value => value != Server.NotAsked).Select(ServerOutcome).ToList();
        if (cell.Portable && !found && cell.Offline)
            servers.Add(SourceOutcome.UnavailableOffline);
        return LibrarySourceDecisions.Decide(cell.Portable, found, servers,
            found ? [DocumentOutcome(cell.First, cell.Offline), DocumentOutcome(cell.Second, cell.Offline)] : [],
            cell.Compilation == Compilation.Refused, MissingMethodlessDocuments(cell));
    }

    private static SourceOutcome[] MissingMethodlessDocuments(Cell cell) => new[] { cell.OptionalFirst, cell.OptionalSecond }
        .Where(value => value is not (OptionalDocument.NotRead or OptionalDocument.Present))
        .Select(value => value switch
        {
            OptionalDocument.StableAbsence => SourceOutcome.Persistent,
            OptionalDocument.Transient => SourceOutcome.Transient,
            OptionalDocument.UnavailableOffline => SourceOutcome.UnavailableOffline,
            _ => throw new InvalidOperationException(value.ToString())
        }).ToArray();

    [Fact]
    public void Every_cell_gets_its_exact_expected_answer()
    {
        var expectations = Cells.ToDictionary(cell => cell, Expected);
        Assert.Equal(Cells.Length, expectations.Count);
        foreach (var cell in Cells)
        {
            Assert.True(expectations[cell] == Decide(cell), $"{cell}: expected {expectations[cell]}; got {Decide(cell)}");
            if (cell.Compilation != Compilation.NotReached)
            {
                var prepared = new LibrarySourceResult(LibraryCodeKind.OriginalSources, null, [], [],
                    Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview, "matrix")
                { MissingMethodlessDocuments = MissingMethodlessDocuments(cell) };
                var compiled = LibrarySourceDecisions.ForCompilation(prepared, cell.Compilation == Compilation.Refused);
                Assert.True(expectations[cell] == (compiled.Kind, compiled.Reason), $"{cell}: compilation returned {compiled}");
            }
        }
    }

    [Fact]
    public void No_offline_cell_asks_a_server()
    {
        Assert.All(Cells.Where(cell => cell.Offline), cell =>
        {
            Assert.Equal(Server.NotAsked, cell.Microsoft);
            Assert.Equal(Server.NotAsked, cell.Nuget);
        });
        Assert.All(Cells.Where(cell => !cell.Portable || LocalFound(cell.Local)), cell =>
        {
            Assert.Equal(Server.NotAsked, cell.Microsoft);
            Assert.Equal(Server.NotAsked, cell.Nuget);
        });
        Assert.All(Cells.Where(cell => !cell.Checksum || cell.Microsoft == Server.Pdb), cell => Assert.Equal(Server.NotAsked, cell.Nuget));
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
        Assert.Equal(new[] { false, true }, Cells.Select(cell => cell.Offline).Distinct().Order());
        Assert.Equal(new[] { false, true }, Cells.Select(cell => cell.Portable).Distinct().Order());
        Assert.Equal(new[] { false, true }, Cells.Select(cell => cell.Checksum).Distinct().Order());
        Assert.Equal(Enum.GetValues<Compilation>(), Cells.Select(cell => cell.Compilation).Distinct().Order());
        Assert.Equal(Enum.GetValues<LocalPdb>(), Cells.Select(cell => cell.Local).Distinct().Order());
        Assert.Equal(Enum.GetValues<Server>(), Cells.Select(cell => cell.Microsoft).Distinct().Order());
        Assert.Equal(Enum.GetValues<Server>(), Cells.Select(cell => cell.Nuget).Distinct().Order());
        Assert.Equal(Enum.GetValues<Document>(), Cells.Select(cell => cell.First).Distinct().Order());
        Assert.Equal(Enum.GetValues<Document>(), Cells.Select(cell => cell.Second).Distinct().Order());
        Assert.Equal(Enum.GetValues<OptionalDocument>(), Cells.Select(cell => cell.OptionalFirst).Distinct().Order());
        Assert.Equal(Enum.GetValues<OptionalDocument>(), Cells.Select(cell => cell.OptionalSecond).Distinct().Order());
        foreach (var offline in new[] { false, true })
        foreach (var first in OptionalDocuments(offline))
        foreach (var second in OptionalDocuments(offline))
        foreach (var compilation in new[] { Compilation.Compiled, Compilation.Refused })
            Assert.Contains(Cells, cell => cell.Offline == offline && cell.OptionalFirst == first && cell.OptionalSecond == second &&
                cell.Compilation == compilation);
        Assert.All(Cells.Where(cell => cell.Compilation != Compilation.NotReached), cell =>
        {
            Assert.True(CanCompile(cell.First, cell.Offline));
            Assert.True(CanCompile(cell.Second, cell.Offline));
        });
    }

    [Fact]
    public async Task Five_loopback_cells_agree_with_the_stage_decision()
    {
        await using var server = new LoopbackSymbolServer();
        using (var library = new SourceLibrary(server.Address))
        {
            library.Serve(server, "nuget");
            using var sources = Sources(library, server);
            AssertDecision(await sources.GetAsync(library.Dll), true, [SourceOutcome.Persistent, SourceOutcome.Received],
                [SourceOutcome.Received, SourceOutcome.Received]);
            Assert.Contains(server.Requests, request => request.Path == "/nuget/" + library.SymbolKey && request.Headers.ContainsKey("SymbolChecksum"));
        }
        server.Requests.Clear();
        using (var library = new SourceLibrary(server.Address, adjacent: true))
        {
            server.Replies["/sources/file0.cs"] = new(503);
            server.Replies["/sources/file1.cs"] = new(410);
            using var sources = new LibrarySources(false, library.Cache, httpsOnly: false, timeout: Timeout.InfiniteTimeSpan)
            { RequestTime = new SourceRetryClock() };
            AssertDecision(await sources.GetAsync(library.Dll), true, [], [SourceOutcome.Transient, SourceOutcome.Persistent]);
            Assert.Equal(5, server.Requests.Count);
        }
        server.Requests.Clear();
        using (var library = new SourceLibrary(server.Address, adjacent: true, optionalDocument: true))
        {
            library.Serve(server);
            using (var online = Sources(library, server))
                Assert.Equal(LibraryCodeKind.OriginalSources, (await online.GetAsync(library.Dll)).Kind);
            var optional = library.Documents["/src/AssemblyInfo.cs"];
            // Retain both method documents and the PDB, but remove the optional document from this temporary cache.
            foreach (var file in Directory.GetFiles(library.Cache, "*.source", SearchOption.AllDirectories))
                if (File.ReadAllBytes(file).SequenceEqual(optional)) File.Delete(file);
            server.Requests.Clear();
            using var offline = Sources(library, server, offline: true);
            var result = await offline.GetAsync(library.Dll);
            AssertDecision(result, true, [], [SourceOutcome.Received, SourceOutcome.Received]);
            Assert.Equal(2, result.Documents.Count);
            Assert.Equal(SourceOutcome.UnavailableOffline, Assert.Single(result.MissingMethodlessDocuments));
            Assert.Equal(new LibraryCode(LibraryCodeKind.OriginalSources, null), LibrarySourceDecisions.ForCompilation(result));
            Assert.Equal(new LibraryCode(LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Offline),
                LibrarySourceDecisions.ForCompilation(result, sourcesRejected: true));
            Assert.Empty(server.Requests);
        }
        await using var nuget = new LoopbackSymbolServer(localhost: true);
        using (var previous = new SourceLibrary(server.Address, crlf: true))
        using (var library = new SourceLibrary(server.Address))
        {
            server.Requests.Clear();
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.RequestReceived = request => { if (request.Path == "/symbols/" + previous.SymbolKey) waiting.TrySetResult(); };
            server.Replies["/symbols/" + previous.SymbolKey] = new(Delay: Timeout.InfiniteTimeSpan);
            var clock = new FakeClock();
            using var sources = new LibrarySources(false, library.Cache, new Uri(server.Address, "symbols/"),
                new Uri(nuget.Address, "nuget/"), httpsOnly: false, timeout: TimeSpan.FromMilliseconds(500)) { RequestTime = clock };
            var read = sources.GetAsync(previous.Dll);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(read.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            AssertDecision(await read.WaitAsync(TimeSpan.FromSeconds(30)), false, [SourceOutcome.Transient, SourceOutcome.Persistent], []);
            AssertDecision(await sources.GetAsync(library.Dll).WaitAsync(TimeSpan.FromSeconds(30)), false, [SourceOutcome.Transient, SourceOutcome.Persistent], []);
            Assert.Single(server.Requests);
            Assert.Equal(2, nuget.Requests.Count);
        }
        server.RequestReceived = null;
        server.Requests.Clear();
        using (var library = new SourceLibrary(server.Address, adjacent: true, partialSourceLink: true))
        {
            library.Serve(server);
            using var sources = Sources(library, server);
            AssertDecision(await sources.GetAsync(library.Dll), true, [], [SourceOutcome.Received, SourceOutcome.NoSourceAddress]);
            Assert.Equal("/sources/file0.cs", Assert.Single(server.Requests).Path);
        }
    }

    private static LibrarySources Sources(SourceLibrary library, LoopbackSymbolServer server, bool offline = false) =>
        new(offline, library.Cache, new Uri(server.Address, "symbols/"), new Uri(server.Address, "nuget/"), httpsOnly: false);

    private static void AssertDecision(LibrarySourceResult result, bool pdbFound, SourceOutcome[] pdb, SourceOutcome[] documents) =>
        Assert.Equal(LibrarySourceDecisions.Decide(true, pdbFound, pdb, documents), (result.Kind, result.Reason));
}
