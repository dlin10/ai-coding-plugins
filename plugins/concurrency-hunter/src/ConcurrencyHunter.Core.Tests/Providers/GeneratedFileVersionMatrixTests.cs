using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json.Nodes;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Solving;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;
using GenerationCauses = ConcurrencyHunter.Analysis.GenerationCauses;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>R2's file rule: every version, key, entry and mode combination, and the changes another writer can make before the locked read.
/// Each cell reaches only B through the analyzer, using a fake generator. No cell stands for a broader set of inputs.</summary>
public sealed class GeneratedFileVersionMatrixTests
{
    private const string PACKAGE = "Acme.Lib";
    private const string A = "M:Acme.Api.A(System.Object)";
    private const string B = "M:Acme.Api.B(System.Object)";
    private const string OTHER_FINGERPRINT = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Lazy<byte[]> Image = new(() => EmittedAssemblies.Image(PACKAGE, ReferenceScopes.VERSION_ATTRIBUTE + """
        namespace Acme
        {
            public static class Api
            {
                public static void A(object value) { }
                public static void B(object value) { }
            }
        }
        """));

    private enum FileVersion { Current, OtherFingerprint, FormerFormat, OtherSchemaNumber, NoGenerator, GeneratorNotString }
    private enum FileKey { Names, OtherKey, NoHeader }
    private enum Entries { Valid, OneInvalid }
    private enum State { Product, Missing, Malformed }
    private enum Transition
    {
        Unchanged,
        CurrentToOtherFingerprint,
        OtherFingerprintToCurrent,
        MissingToCurrent,
        CurrentToMissing,
        OtherFingerprintToOtherKey,
        OtherFingerprintToMalformed,
        CurrentToMalformed
    }
    private enum Answer { Stored, Generated, NotGenerated }
    private enum Diagnostic { None, Unreadable, OtherVersion, InvalidB }
    private enum Write { Unchanged, Replace, Merge }

    private sealed record Cell(State State, FileVersion? Version, FileKey? Key, Entries? Entries, ModelGeneration Mode,
                               Transition Transition = Transition.Unchanged)
    {
        public override string ToString() => $"{State} / {Version} / {Key} / {Entries} / {Mode} / {Transition}";
    }

    private sealed record Expectation(Answer Answer, Diagnostic Diagnostic, Write Write);
    private sealed record Observation(Cell Cell, AnalysisResult Result, string[] Requested, byte[]? Before, byte[]? AtRead, byte[]? After, int Reads);

    private static readonly IReadOnlyList<Cell> Cells =
        [.. new[] { ModelGeneration.Generate, ModelGeneration.ReadOnly }.SelectMany(mode =>
            Enum.GetValues<FileVersion>().SelectMany(version => Enum.GetValues<FileKey>().SelectMany(key =>
                Enum.GetValues<Entries>().Select(entries => new Cell(State.Product, version, key, entries, mode))))
                .Concat(new[] { State.Missing, State.Malformed }.Select(state => new Cell(state, null, null, null, mode)))),
         .. Enum.GetValues<Transition>().Where(transition => transition != Transition.Unchanged).Select(TransitionCell)];

    // The three assertions share observations, so each of the 83 analyzer runs executes once, with an isolated repository per cell.
    private static readonly Lazy<Task<IReadOnlyList<Observation>>> Observations = new(ObserveAll);

    [Fact]
    public async Task Every_cell_gets_its_expected_answer()
    {
        foreach (var actual in await Observations.Value)
        {
            try
            {
                AssertAnswer(actual);
                AssertDiagnostics(actual);
                AssertFile(actual);
            }
            catch (Exception error)
            {
                Assert.Fail($"{actual.Cell}: {error}");
            }
        }
    }

    [Fact]
    public async Task No_cell_applies_an_answer_of_another_version()
    {
        foreach (var actual in await Observations.Value)
        {
            var cell = actual.Cell;
            if (cell.State != State.Product || cell.Version == FileVersion.Current)
                continue;
            var coverage = Assert.Single(actual.Result.Coverage);
            var generated = cell.Mode == ModelGeneration.Generate;
            Assert.True(coverage.Skips[CoverageCounters.KNOWN_CALL_GENERATED] == (generated ? 1 : 0) &&
                        coverage.Skips[CoverageCounters.GENERATED_IN_RUN] == (generated ? 1 : 0) &&
                        actual.Requested.SequenceEqual(generated ? [B] : Array.Empty<string>()),
                        $"{cell}: an answer of another version was used");
        }
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        Assert.NotEqual(GeneratorVersion.Fingerprint, OTHER_FINGERPRINT);
        Assert.Equal(83, Cells.Count);
        Assert.Equal(83, Cells.Distinct().Count());
        var stable = Cells.Where(cell => cell.Transition == Transition.Unchanged).ToArray();
        Assert.Equal(76, stable.Length);
        Assert.Equal(72, stable.Count(cell => cell.State == State.Product));
        Assert.Equal(Enum.GetValues<FileVersion>().Order(), stable.Where(cell => cell.Version.HasValue).Select(cell => cell.Version!.Value).Distinct().Order());
        Assert.Equal(Enum.GetValues<FileKey>().Order(), stable.Where(cell => cell.Key.HasValue).Select(cell => cell.Key!.Value).Distinct().Order());
        Assert.Equal(Enum.GetValues<Entries>().Order(), stable.Where(cell => cell.Entries.HasValue).Select(cell => cell.Entries!.Value).Distinct().Order());
        Assert.Equal(Enum.GetValues<State>().Order(), stable.Select(cell => cell.State).Distinct().Order());
        Assert.Equal(new[] { ModelGeneration.Generate, ModelGeneration.ReadOnly }.Order(), stable.Select(cell => cell.Mode).Distinct().Order());
        Assert.Equal(Enum.GetValues<Transition>().Order(), Cells.Select(cell => cell.Transition).Distinct().Order());
        foreach (var version in Enum.GetValues<FileVersion>())
        foreach (var key in Enum.GetValues<FileKey>())
        foreach (var entries in Enum.GetValues<Entries>())
        foreach (var mode in new[] { ModelGeneration.Generate, ModelGeneration.ReadOnly })
            Assert.Single(stable, cell => cell == new Cell(State.Product, version, key, entries, mode));
        foreach (var state in new[] { State.Missing, State.Malformed })
        foreach (var mode in new[] { ModelGeneration.Generate, ModelGeneration.ReadOnly })
            Assert.Single(stable, cell => cell == new Cell(state, null, null, null, mode));
        foreach (var transition in Enum.GetValues<Transition>().Where(value => value != Transition.Unchanged))
            Assert.Equal(TransitionCell(transition), Assert.Single(Cells, cell => cell.Transition == transition));
    }

    /// <summary>R2 in its decision order; write expectations refer to the first read until a transition supplies a different locked read.</summary>
    /// <param name="cell">The initial file state and the run's mode.</param>
    private static Expectation Expected(Cell cell)
    {
        var generate = cell.Mode == ModelGeneration.Generate;
        var absent = generate ? Answer.Generated : Answer.NotGenerated;
        // R2: malformed JSON and a header that does not name the key are unreadable before any version or entry decision.
        if (cell.State == State.Malformed || cell.State == State.Product && cell.Key != FileKey.Names)
            return new(absent, Diagnostic.Unreadable, Write.Unchanged);
        // R2: missing files have no diagnostic and a generating run creates only the reached member.
        if (cell.State == State.Missing)
            return new(absent, Diagnostic.None, generate ? Write.Replace : Write.Unchanged);
        // R2: another or absent generator version supplies no answers, regardless of entry validity; the first write replaces it whole.
        if (cell.Version != FileVersion.Current)
            return new(absent, Diagnostic.OtherVersion, generate ? Write.Replace : Write.Unchanged);
        // R2: a valid current answer is applied without generation or writing in either mode.
        if (cell.Entries == Entries.Valid)
            return new(Answer.Stored, Diagnostic.None, Write.Unchanged);
        // R2: an invalid B is diagnosed and absent; generation replaces B alone, preserving A of the same version.
        return new(absent, Diagnostic.InvalidB, generate ? Write.Merge : Write.Unchanged);
    }

    private static Cell TransitionCell(Transition transition) => transition switch
    {
        Transition.CurrentToOtherFingerprint or Transition.CurrentToMissing or Transition.CurrentToMalformed =>
            new(State.Product, FileVersion.Current, FileKey.Names, Entries.OneInvalid, ModelGeneration.Generate, transition),
        Transition.OtherFingerprintToCurrent or Transition.OtherFingerprintToOtherKey or Transition.OtherFingerprintToMalformed =>
            new(State.Product, FileVersion.OtherFingerprint, FileKey.Names, Entries.Valid, ModelGeneration.Generate, transition),
        Transition.MissingToCurrent => new(State.Missing, null, null, null, ModelGeneration.Generate, transition),
        _ => throw new ArgumentOutOfRangeException(nameof(transition))
    };

    private static void AssertAnswer(Observation actual)
    {
        var expected = Expected(actual.Cell);
        var coverage = Assert.Single(actual.Result.Coverage);
        Assert.Equal(expected.Answer == Answer.Generated ? new[] { B } : [], actual.Requested);
        Assert.Equal(expected.Answer == Answer.NotGenerated ? 0 : 1, coverage.Skips[CoverageCounters.KNOWN_CALL_GENERATED]);
        Assert.Equal(expected.Answer == Answer.Generated ? 1 : 0, coverage.Skips[CoverageCounters.GENERATED_IN_RUN]);
        Assert.Equal(expected.Answer == Answer.NotGenerated ? new[] { (B, GenerationCauses.NOT_GENERATED) } : [],
                     coverage.GenerationRefusals.Select(refusal => (refusal.MemberId, refusal.Cause)));
        Assert.Equal(expected.Answer == Answer.NotGenerated ? new[] { new WantedGeneration(B, Key().Text) } : [], actual.Result.GenerationWanted);
    }

    private static void AssertDiagnostics(Observation actual)
    {
        var expected = Expected(actual.Cell).Diagnostic;
        var unreadableAtRead = actual.Cell.Transition is Transition.OtherFingerprintToOtherKey or Transition.OtherFingerprintToMalformed or Transition.CurrentToMalformed;
        var diagnostics = Assert.Single(actual.Result.Coverage).Diagnostics.Where(text => text.StartsWith("library-models: generated file ", StringComparison.Ordinal)).ToArray();
        Assert.Equal((expected == Diagnostic.None ? 0 : 1) + (unreadableAtRead ? 1 : 0), diagnostics.Length);
        if (expected != Diagnostic.None)
        {
            var diagnostic = diagnostics[0];
            Assert.Contains(Key().RelativePath!, diagnostic, StringComparison.Ordinal);
            switch (expected)
            {
                case Diagnostic.OtherVersion:
                    Assert.EndsWith("written by another generator version; its answers are not used.", diagnostic);
                    break;
                case Diagnostic.InvalidB:
                    Assert.Contains(B, diagnostic, StringComparison.Ordinal);
                    Assert.Contains("opaque", diagnostic, StringComparison.Ordinal);
                    break;
                case Diagnostic.Unreadable:
                    Assert.Contains(actual.Cell.State == State.Malformed ? "BytePositionInLine" :
                                    actual.Cell.Key == FileKey.OtherKey ? "assembly version" : "'assembly'", diagnostic, StringComparison.Ordinal);
                    Assert.DoesNotContain("another generator version", diagnostic, StringComparison.Ordinal);
                    break;
            }
        }
        if (unreadableAtRead)
            Assert.Contains(actual.Cell.Transition == Transition.OtherFingerprintToOtherKey ? "assembly version" : "BytePositionInLine",
                            diagnostics[^1], StringComparison.Ordinal);
    }

    private static void AssertFile(Observation actual)
    {
        var cell = actual.Cell;
        var write = cell.Transition switch
        {
            Transition.CurrentToOtherFingerprint or Transition.CurrentToMissing => Write.Replace,
            Transition.OtherFingerprintToCurrent or Transition.MissingToCurrent => Write.Merge,
            Transition.OtherFingerprintToOtherKey or Transition.OtherFingerprintToMalformed or Transition.CurrentToMalformed => Write.Unchanged,
            _ => Expected(cell).Write
        };
        var writes = cell.Mode == ModelGeneration.Generate && Expected(cell).Answer == Answer.Generated && Expected(cell).Diagnostic != Diagnostic.Unreadable;
        Assert.Equal(writes ? 1 : 0, actual.Reads);
        if (cell.Transition == Transition.Unchanged)
            Assert.Equal(actual.Before, actual.AtRead);
        if (write == Write.Unchanged)
        {
            Assert.Equal(actual.AtRead, actual.After);
            return;
        }

        Assert.NotNull(actual.After);
        var document = JsonNode.Parse(actual.After)!.AsObject();
        Assert.Equal(2, document["schemaVersion"]!.GetValue<int>());
        Assert.Equal(GeneratorVersion.Fingerprint, document["generator"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(Header(Key()), document["assembly"]), "The written header does not name the file's key.");
        var entries = document["members"]!.AsArray().ToDictionary(entry => entry!["member"]!.GetValue<string>(), entry => entry!);
        Assert.Equal(write == Write.Merge ? new[] { A, B } : [B], entries.Keys.Order(StringComparer.Ordinal));
        Assert.True(JsonNode.DeepEquals(Entry(B, generated: true), entries[B]), "The generated answer B was not written exactly.");
        if (write == Write.Merge)
        {
            var previous = JsonNode.Parse(actual.AtRead!)!["members"]!.AsArray().Single(entry => entry!["member"]!.GetValue<string>() == A);
            Assert.True(JsonNode.DeepEquals(previous, entries[A]), "The other writer's answer A was not preserved exactly.");
        }
    }

    private static async Task<IReadOnlyList<Observation>> ObserveAll()
    {
        using var install = new GenerationInstall();
        install.SharedFramework("8.0.5");
        ReferenceScopes.AddAsset(install, PACKAGE, "1.0.0", "net8.0", PACKAGE, Image.Value);
        var options = new FixtureOptions
        {
            MetadataReferences =
            [
                ReferenceScopes.Reference(Image.Value, ReferenceScopes.PackagePath(install, PACKAGE, "1.0.0", "lib", "net8.0", PACKAGE)),
                ReferenceScopes.Reference(EmittedAssemblies.Image("Fixture.Pack"), ReferenceScopes.PackFile(install, 8, "Microsoft.NETCore.App.Ref", "Fixture.Pack.dll"))
            ]
        };
        var solution = FixtureSolution.Create(options, ("Case.cs", Usings + """
            public sealed class State { public int Count; }
            [ApiController]
            public sealed class StateController : ControllerBase
            {
                private readonly State _state;
                public StateController(State state) => _state = state;
                [HttpGet("/state")] public int Get() => _state.Count;
            }
            public sealed class StateWorker : BackgroundService
            {
                private readonly State _state;
                public StateWorker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    Acme.Api.B(_state);
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<StateWorker>();")));
        var observations = new List<Observation>();
        foreach (var cell in Cells)
        {
            var repository = Directory.CreateTempSubdirectory("ch-file-version-matrix-").FullName;
            try
            {
                var path = Key().PathUnder(repository)!;
                var before = Document(cell);
                Put(path, before);
                var atRead = before;
                var reads = 0;
                var requested = new List<string>();
                var setup = new GenerationSetup(cell.Mode, install.Resolver(), (request, _) =>
                {
                    requested.Add(request.MemberId);
                    return Model(request);
                }, () =>
                {
                    reads++;
                    Assert.Equal(before, Bytes(path));
                    atRead = ChangedDocument(cell, before);
                    Put(path, atRead);
                });
                var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, repository, ProviderRegistry.BuiltIn, InterproceduralPairing.Pair, AnalysisLimits.Default,
                                                                Z3ConstraintSolver.Create, setup, CancellationToken.None);
                observations.Add(new(cell, result, requested.ToArray(), before, atRead, Bytes(path), reads));
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Matrix cell {cell} failed.", error);
            }
            finally
            {
                Directory.Delete(repository, recursive: true);
            }
        }
        return observations;
    }

    private static byte[]? ChangedDocument(Cell cell, byte[]? before) => cell.Transition switch
    {
        Transition.Unchanged => before,
        Transition.CurrentToMissing => null,
        Transition.CurrentToOtherFingerprint => Document(new(State.Product, FileVersion.OtherFingerprint, FileKey.Names, Entries.Valid, cell.Mode)),
        Transition.OtherFingerprintToCurrent or Transition.MissingToCurrent => Document(new(State.Product, FileVersion.Current, FileKey.Names, Entries.Valid, cell.Mode), onlyA: true),
        Transition.OtherFingerprintToOtherKey => Document(new(State.Product, FileVersion.OtherFingerprint, FileKey.OtherKey, Entries.Valid, cell.Mode)),
        Transition.OtherFingerprintToMalformed or Transition.CurrentToMalformed => Encoding.UTF8.GetBytes("{\"schemaVersion\":"),
        _ => throw new ArgumentOutOfRangeException(nameof(cell))
    };

    private static byte[]? Document(Cell cell, bool onlyA = false)
    {
        if (cell.State == State.Missing)
            return null;
        if (cell.State == State.Malformed)
            return Encoding.UTF8.GetBytes("{\"schemaVersion\":");
        var members = new JsonArray(Entry(A));
        if (!onlyA)
        {
            var entry = Entry(B);
            if (cell.Entries == Entries.OneInvalid)
            {
                var model = entry["model"]!.AsObject();
                model.Remove("effects");
                model.Remove("fates");
                model["opaque"] = true;
            }
            members.Add(entry);
        }
        var document = new JsonObject
        {
            ["schemaVersion"] = cell.Version == FileVersion.FormerFormat ? 1 : cell.Version == FileVersion.OtherSchemaNumber ? 3 : 2,
            ["members"] = members
        };
        if (cell.Key != FileKey.NoHeader)
        {
            var header = Header(Key());
            if (cell.Key == FileKey.OtherKey)
                header["version"] = "2.0.0.0";
            document["assembly"] = header;
        }
        if (cell.Version is not (FileVersion.FormerFormat or FileVersion.NoGenerator))
            document["generator"] = cell.Version == FileVersion.GeneratorNotString ? JsonValue.Create(42) :
                                    JsonValue.Create(cell.Version == FileVersion.OtherFingerprint ? OTHER_FINGERPRINT : GeneratorVersion.Fingerprint);
        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    private static JsonObject Header(GeneratedFileKey key) => new()
    {
        ["name"] = key.AssemblyName, ["version"] = key.AssemblyVersion.ToString(4), ["mvid"] = key.Mvid!.Value.ToString("D"),
        ["package"] = key.Package, ["packageVersion"] = key.PackageVersion
    };

    private static JsonObject Entry(string member, bool generated = false) =>
        new() { ["member"] = member, ["model"] = ModelEntryWriter.Write(LibraryModel(member, generated)) };

    private static LibraryModel LibraryModel(string member, bool generated) =>
        new(member, [new SupportedAssemblyVersion(PACKAGE, new Version(1, 0, 0, 0), new Version(1, 0, 0, 1))], generated ? [] : [LibraryEffect.DeepReadOf("value")]);

    private static GeneratedAnswer Model(GenerationRequest request) =>
        new(3, request.MemberId, new GenerationAssembly(request.AssemblyName, request.Version, request.PackageId), null, null,
            LibraryModel(request.MemberId, generated: true), null,
            new GenerationRecord(null, null, [], 0, [], new Dictionary<string, string>(), [], 0, [], new Dictionary<string, IReadOnlyList<string>>(), 0, 0));

    private static GeneratedFileKey Key()
    {
        using var pe = new PEReader(new MemoryStream(Image.Value));
        var reader = pe.GetMetadataReader();
        return GeneratedFileKey.ForPackage(PACKAGE, new Version(1, 0, 0, 0), PACKAGE, "1.0.0", reader.GetGuid(reader.GetModuleDefinition().Mvid));
    }

    private static byte[]? Bytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static void Put(string path, byte[]? bytes)
    {
        if (bytes is null)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }
}
