using System.Text.Json.Nodes;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed partial class RunGenerationPassTests
{
    private const string RECEIVER_MEMBER = "M:Acme.D.M(System.Object)";
    private const string RECEIVER_LIBRARY = """
        namespace Acme;
        public class D { public void M(object value) { } }
        public class K : D { }
        public class S : D { }
        public interface J { void N(object value); }
        public interface J1 : J { }
        public interface J2 : J { }
        public interface J3 : J1, J2 { }
        public class Impl : J3 { public void N(object value) { } }
        """;

    public static IEnumerable<object[]> ReceiverRangeCells() =>
        from project in new[] { false, true }
        from inRange in new[] { false, true }
        from incomparable in new[] { false, true }
        from opaque in new[] { false, true }
        select new object[] { project, inRange, incomparable, opaque };

    [Theory]
    [MemberData(nameof(ReceiverRangeCells))]
    public async Task Receiver_key_agreement_excludes_generation_only_for_records_in_range(bool project, bool inRange, bool incomparable, bool opaque)
    {
        const string member = "M:Acme.J.N(System.Object)";
        var solution = ReceiverSolution("Acme.J3 receiver = new Acme.Impl(); receiver.N(_state);");
        var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
        var version = compilation.GetTypeByMetadataName("Acme.J")!.ContainingAssembly.Identity.Version;
        var range = inRange ? Range() : new SupportedAssemblyVersion(PACKAGE, new Version(2, 0, 0, 0), new Version(3, 0, 0, 0));
        var layer = project ? ModelLayer.Project : ModelLayer.BuiltIn;
        LibraryModel Record(string key) => new(member, [range], [], layer, DeclaredOpaque: opaque, ResolvedVersion: version, Receiver: "T:Acme." + key);
        var records = new[] { Record(incomparable ? "J1" : "J"), Record("J2") };
        var models = project ? new LibraryModels(LibraryModels.BuiltIn, records) : new LibraryModels(records, []);
        var match = models.Find(compilation.GetTypeByMetadataName("Acme.J")!.GetMembers("N").OfType<IMethodSymbol>().Single(),
                                compilation, compilation.GetTypeByMetadataName("Acme.J3"));
        Assert.Equal(inRange && (incomparable || opaque), match?.DeclaredOpaque == true);
        var run = ScopePipeline.Run("Fixture", [compilation], [(compilation, null)], ROOT_DIRECTORY, ProviderRegistry.BuiltIn,
                                    models, AnalysisLimits.Default, new(), null, CancellationToken.None);
        using var pass = GenerationPass.Create(new GenerationSetup(ModelGeneration.ReadOnly, _install.Resolver()), _repository, CancellationToken.None)!;

        Assert.Equal(!inRange, pass.Candidates(run, [compilation]).Any(candidate => candidate.Target.MemberId == member));
    }

    [Fact]
    public async Task Call_a_keyed_opaque_record_answers_is_no_generation_target()
    {
        ReceiverRecords((RECEIVER_MEMBER, "T:Acme.K", true));
        var result = await Run(ReceiverSolution("new Acme.K().M(_state);"), ModelGeneration.Generate);

        Assert.Empty(_requested);
        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(0, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
        Assert.Equal(0, Counter(result, CoverageCounters.GENERATED_IN_RUN));
        Assert.Empty(Assert.Single(result.Coverage).GenerationRefusals);
    }

    [Fact]
    public async Task Call_left_opaque_by_disagreeing_keys_is_no_generation_target()
    {
        const string member = "M:Acme.J.N(System.Object)";
        ReceiverRecords((member, "T:Acme.J1", false), (member, "T:Acme.J2", false));
        var result = await Run(ReceiverSolution("Acme.J3 receiver = new Acme.Impl(); receiver.N(_state);"), ModelGeneration.Generate);

        Assert.Empty(_requested);
        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(0, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
        Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Empty(Assert.Single(result.Coverage).GenerationRefusals);
    }

    [Fact]
    public async Task Member_with_keyed_and_uncovered_calls_is_a_target_only_through_the_uncovered_one()
    {
        ReceiverRecords((RECEIVER_MEMBER, "T:Acme.K", true));
        var result = await Run(ReceiverSolution("new Acme.K().M(_state); new Acme.D().M(_state);"), ModelGeneration.Generate);

        Assert.Equal([RECEIVER_MEMBER], _requested);
        // The generated answer applies only to the D expression. The K expression keeps its curated opaque answer.
        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
        Assert.Equal(1, Counter(result, CoverageCounters.GENERATED_IN_RUN));
        Assert.Equal(2, _analyses);
    }

    [Fact]
    public async Task Member_whose_calls_no_keyed_record_covers_stays_a_generation_target()
    {
        ReceiverRecords((RECEIVER_MEMBER, "T:Acme.K", true));
        var result = await Run(ReceiverSolution("new Acme.S().M(_state);"), ModelGeneration.Generate);

        Assert.Equal([RECEIVER_MEMBER], _requested);
        Assert.Equal(0, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
        Assert.Equal(1, Counter(result, CoverageCounters.GENERATED_IN_RUN));
    }

    /// <summary>The existing run fixture with an instance-member package in place of its static API.</summary>
    /// <param name="work">The hosted service's statements.</param>
    private Solution ReceiverSolution(string work) => Solution(work, Emit(PACKAGE, RECEIVER_LIBRARY));

    /// <summary>Writes receiver records and keyless constructor records, so generation requests isolate the instance members.</summary>
    /// <param name="records">The member, receiver key and opaque flag of each curated record.</param>
    private void ReceiverRecords(params (string Member, string Receiver, bool Opaque)[] records)
    {
        var entries = new JsonArray();
        foreach (var type in new[] { "D", "K", "S", "Impl" })
            entries.Add(new JsonObject { ["member"] = $"M:Acme.{type}.#ctor", ["effects"] = new JsonObject() });
        foreach (var (member, receiver, opaque) in records)
        {
            var entry = new JsonObject { ["member"] = member, ["receiver"] = receiver };
            if (opaque)
                entry["opaque"] = true;
            else
                entry["effects"] = new JsonObject();
            entries.Add(entry);
        }
        var file = new JsonObject { ["schemaVersion"] = 1, ["assemblies"] = new JsonArray(PACKAGE), ["models"] = entries };
        var folder = Directory.CreateDirectory(Path.Combine(_repository, ".concurrency-hunter", "models")).FullName;
        File.WriteAllText(Path.Combine(folder, "receivers.json"), file.ToJsonString());
    }
}
