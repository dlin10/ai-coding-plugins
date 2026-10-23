using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Generation.ModelEvals;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The model evals of G-7: the generator on the 18 gold members of <c>System.Linq</c>, <c>System.Security.Claims</c>, Polly
/// and Google.Protobuf, against the gold fates and the recorded snapshot. They run only under <c>CH_MODEL_EVALS=1</c>, and there a
/// missing input fails them. They run alone, after every parallel test: one of them measures wall time.</summary>
[Collection(COLLECTION)]
public sealed class ModelEvalTests
{
    private const string RECORDING_SNAPSHOT = "{\"exact\":11,\"fateGoldEntries\":3,\"effectsExact\":8,\"linqExact\":{\"System.Linq\":12,\"System.Linq.Queryable\":1}," +
                                              "\"accessorsExact\":5}";
    public const string COLLECTION = "Model evals";

    [RequiresModelEvalsFact]
    public void Classified_fates_have_no_unsafe_narrowing()
    {
        var unsafeFates = Scored(InstalledRun).Where(score => !IsSafe(score.Answered, score.Gold))
                                              .Select(score => $"{score.Member}({score.Parameter}): gold {Show(score.Gold)}, answered {Show(score.Answered)}")
                                              .ToArray();

        Assert.True(unsafeFates.Length == 0, $"{unsafeFates.Length} unsafe narrowing(s):\n" + string.Join("\n", unsafeFates));
    }

    [RequiresModelEvalsFact]
    public void Exact_fates_meet_the_floor()
    {
        // A4: at least 14 of the 24 exact, or, when fewer are, no fewer than the snapshot recorded; recording sets that floor.
        var scores = Scored(InstalledRun);
        EvalReport.Write(EvalReport.Folder, "members", Lines(InstalledRun));
        var exact = Exact(InstalledRun);
        int? recorded = Environment.GetEnvironmentVariable(RECORD_VARIABLE) == "1" ? exact
                      : File.Exists(SnapshotPath) ? RecordedExact(File.ReadAllText(SnapshotPath)) : null;
        var floor = Math.Min(EXACT_FLOOR, recorded ?? EXACT_FLOOR);

        Assert.Equal(GOLD_PARAMETERS, scores.Count);
        Assert.True(exact >= floor,
                    $"{exact} of {scores.Count} exact, under the floor {floor} (14, or the snapshot's {recorded?.ToString() ?? "none"}); not exact:\n" +
                    string.Join("\n", scores.Where(score => !IsExact(score.Answered, score.Gold))
                                            .Select(score => $"{score.Member}({score.Parameter}): gold {Show(score.Gold)}, answered {Show(score.Answered)}" +
                                                             (score.Reason is null ? "" : $" ({score.Reason})"))));
    }

    [RequiresModelEvalsFact]
    public void Entries_pass_the_project_reader()
    {
        foreach (var answer in InstalledRun.Answers.Where(answer => answer.Answer.Model is not null))
        {
            var files = ProjectModelFiles.Read("generated.json", ModelEntryWriter.File(answer.Answer.Model!));
            Assert.Empty(files.Rejections);
            var entry = Assert.Single(files.Entries);
            var (member, compilation) = DeclaringMember(answer);
            Assert.Null(ProjectModelResolver.EntryRejection(entry, member, compilation));
        }
    }

    [RequiresModelEvalsFact]
    public void Entry_fates_are_the_classified_delegate_fates()
    {
        foreach (var answer in InstalledRun.Answers.Where(answer => answer.Answer.Model is not null))
        {
            var (member, _) = DeclaringMember(answer);
            var expected = member.Parameters.Where(parameter => parameter.Type.TypeKind == TypeKind.Delegate)
                                 .Select(parameter => parameter.Name).Order(StringComparer.Ordinal).ToArray();
            var actual = answer.Answer.Model!.Fates.OrderBy(fate => fate.Parameter, StringComparer.Ordinal).ToArray();

            Assert.Equal(expected, actual.Select(fate => fate.Parameter));
            foreach (var fate in actual)
            {
                var classified = Assert.Contains(fate.Parameter, answer.Answer.Classified!);
                Assert.Equal(classified.Fate, LibraryFate.Text(fate.Kind));
                Assert.Equal(classified.Holder, fate.Holder is null ? null : fate.Holder == LibraryHolderKind.Result ? "result" : "this");
            }
        }
    }

    [RequiresModelEvalsFact]
    public void Fate_gold_members_with_an_entry_meet_the_recorded_count()
    {
        var entries = FateGoldEntries(InstalledRun);
        var recorded = RecordedCount("fateGoldEntries");

        Assert.True(entries >= recorded, $"{entries} fate-gold members have an entry, under the recorded count {recorded}");
    }

    [Fact]
    public void Recording_cannot_lower_the_fate_gold_entry_floor() => AssertRecordingPreservesFloor("fateGoldEntries");

    [Fact]
    public void Recording_cannot_lower_the_accessor_floor() => AssertRecordingPreservesFloor("accessorsExact");

    [Fact]
    public void Recording_cannot_lower_the_exact_floor() => AssertRecordingPreservesFloor("exact");

    [Fact]
    public void Eval_report_lists_each_answer_and_its_verdict()
    {
        // A fate member with an exact, a wider and an unsafe parameter; whole entries exact, unsafe and wider; accessors exact by an
        // unclassified fate and no entry for an opaque truth, wider by a fate, unsafe by a fate, unsafe by an entry.
        var members = new EvalRun([
            new EvalAnswer(new GoldMember("A", "M:Lib.C.A(System.Action,System.Action)", "invoke-now", null, ["a", "b"], "Lib", "1.0"),
                           Answer(new Dictionary<string, ClassifiedFate> { ["a"] = new("invoke-now", null) }, null), true),
            new EvalAnswer(new GoldMember("B", "M:Lib.C.B(System.Action)", "invoke-now", null, ["c"], "Lib", "1.0"),
                           Answer(new Dictionary<string, ClassifiedFate> { ["c"] = HolderResult }, null), true)
        ], []);
        var input = new EvalInput("Lib", "1.0", null, "net10.0");
        var model = new LibraryModel("M:Lib.C.E(Lib.Item)", [new ConcurrencyHunter.Providers.SupportedAssemblyVersion("Lib", new Version(1, 0, 0, 0),
                                                                                                                      new Version(2, 0, 0, 0))], []);
        var effects = new WholeEntryRun([
            new WholeEntryAnswer(new WholeEntryGold("M:Lib.C.E(Lib.Item)", input, EntryTruth.Opaque, "Lib"), Answer(null, null), true),
            new WholeEntryAnswer(new WholeEntryGold("M:Lib.C.F(Lib.Item)", input, EntryTruth.Opaque, "Lib"), Answer(null, null, model), true),
            new WholeEntryAnswer(new WholeEntryGold("M:Lib.C.G(Lib.Item)", input, EntryTruth.Entry(model), "Other"), Answer(null, null), true)
        ], []);
        var unknown = new Dictionary<string, ClassifiedFate> { ["value"] = Unknown };
        var notRun = new Dictionary<string, ClassifiedFate> { ["value"] = new(FateClassifier.NOT_RUN, null) };
        var invokeNow = new Dictionary<string, ClassifiedFate> { ["value"] = new(FateClassifier.INVOKE_NOW, null) };
        var accessors = new WholeEntryRun([
            new WholeEntryAnswer(new WholeEntryGold("M:Lib.C.add_E(System.Action)", input, EntryTruth.Opaque, "Lib", unknown), Answer(null, null), true),
            new WholeEntryAnswer(new WholeEntryGold("M:Lib.C.remove_E(System.Action)", input, EntryTruth.Opaque, "Lib", notRun), Answer(unknown, null), true),
            new WholeEntryAnswer(new WholeEntryGold("M:Lib.C.add_F(System.Action)", input, EntryTruth.Opaque, "Lib", unknown), Answer(invokeNow, null), true),
            new WholeEntryAnswer(new WholeEntryGold("M:Lib.C.set_P(Lib.Item)", input, EntryTruth.Opaque, "Lib", new Dictionary<string, ClassifiedFate>()),
                                 Answer(null, null, model), true)
        ], []);
        var folder = Path.Combine(Path.GetTempPath(), $"ch-report-{Guid.NewGuid():N}");
        try
        {
            EvalReport.Write(null, "members", Lines(members));
            Assert.False(Directory.Exists(folder));

            EvalReport.Write(folder, "members", Lines(members));
            EvalReport.Write(folder, "effects", WholeEntryEvals.Lines(effects));
            EvalReport.Write(folder, "accessors", AccessorEvals.Lines(accessors));
            EvalReport.WriteNames(folder, "entry-matrix-wider.json", ["Setter/Item/Store"]);
            var memberLines = EvalReport.Read(Path.Combine(folder, "members.json"));
            var effectLines = EvalReport.Read(Path.Combine(folder, "effects.json"));
            var accessorLines = EvalReport.Read(Path.Combine(folder, "accessors.json"));

            Assert.Equal([new ReportLine("M:Lib.C.A(System.Action,System.Action)", EvalReport.EXACT),
                          new ReportLine("M:Lib.C.A(System.Action,System.Action)", EvalReport.WIDER),
                          new ReportLine("M:Lib.C.B(System.Action)", EvalReport.UNSAFE)], memberLines);
            Assert.Equal([new ReportLine("M:Lib.C.E(Lib.Item)", EvalReport.EXACT, "Lib"), new ReportLine("M:Lib.C.F(Lib.Item)", EvalReport.UNSAFE, "Lib"),
                          new ReportLine("M:Lib.C.G(Lib.Item)", EvalReport.WIDER, "Other")], effectLines);
            Assert.Equal([new ReportLine("M:Lib.C.add_E(System.Action)", EvalReport.EXACT), new ReportLine("M:Lib.C.remove_E(System.Action)", EvalReport.WIDER),
                          new ReportLine("M:Lib.C.add_F(System.Action)", EvalReport.UNSAFE), new ReportLine("M:Lib.C.set_P(Lib.Item)", EvalReport.UNSAFE)],
                         accessorLines);
            Assert.Equal(["Setter/Item/Store"],
                         System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "entry-matrix-wider.json")))!.AsArray()
                                                        .Select(node => (string)node!));

            Assert.Equal(EvalReport.Exact(memberLines), Exact(members));
            Assert.Equal(EvalReport.Exact(effectLines), WholeEntryEvals.Exact(effects));
            Assert.Equal(new Dictionary<string, int> { ["Lib"] = 1, ["Other"] = 0 }, WholeEntryEvals.ExactByGroup(effects));
            Assert.Equal(EvalReport.Exact(accessorLines), AccessorEvals.Exact(accessors));
            Assert.Equal([1, 1, 1], new[] { Exact(members), WholeEntryEvals.Exact(effects), AccessorEvals.Exact(accessors) });
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot(members, effects, effects, accessors))!;
            Assert.Equal(1, (int)snapshot["exact"]!);
            Assert.Equal(1, (int)snapshot["effectsExact"]!);
            Assert.Equal(1, (int)snapshot["accessorsExact"]!);
            Assert.Equal(4, snapshot["accessors"]!.AsArray().Count);
            // bounded: the four fixed snapshot sections.
            foreach (var section in new[] { "members", "effects", "linq", "accessors" })
            {
                Assert.All(snapshot[section]!.AsArray(), answer =>
                {
                    Assert.Equal("", (string?)answer!["detail"]);
                    Assert.Empty(answer["causes"]!.AsArray());
                });
            }
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_snapshot_answer_keeps_its_detail_and_cause_codes()
    {
        var answer = Answer(null, null) with
        {
            Detail = "the probe reached an unseen member",
            Causes = [new GenerationCause(ModelReasons.UNKNOWN_TOUCH, ModelCauses.PROBE_HANDED_TO_UNSEEN, "probe"),
                      new GenerationCause(ModelReasons.UNKNOWN_TOUCH, ModelCauses.ARGUMENT_MEMBER_UNSEEN, "argument"),
                      new GenerationCause(ModelReasons.UNKNOWN_TOUCH, ModelCauses.PROBE_HANDED_TO_UNSEEN, "another probe")]
        };
        var gold = new GoldMember("M", answer.Member, "invoke-now", null, [], "Lib", "1.0");
        var run = new EvalRun([new EvalAnswer(gold, answer, true),
                               new EvalAnswer(gold, Answer(null, null) with { Member = "M:Lib.C.N(System.Action)" }, true)], []);

        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot(run, new WholeEntryRun([], []), new WholeEntryRun([], []),
                                                                     new WholeEntryRun([], [])))!;
        var entries = snapshot["members"]!.AsArray();
        Assert.Equal(answer.Detail, (string?)entries[0]!["detail"]);
        Assert.Equal([ModelCauses.ARGUMENT_MEMBER_UNSEEN, ModelCauses.PROBE_HANDED_TO_UNSEEN],
                     entries[0]!["causes"]!.AsArray().Select(code => (string)code!));
        Assert.Equal("", (string?)entries[1]!["detail"]);
        Assert.Empty(entries[1]!["causes"]!.AsArray());

        var recorded = snapshot.ToJsonString();
        entries[0]!["detail"] = "another detail";
        Assert.Contains(answer.Member, Assert.Single(Differences(recorded, snapshot.ToJsonString())));
        entries[0]!["detail"] = answer.Detail;
        entries[0]!["causes"]!.AsArray().Clear();
        Assert.Contains(answer.Member, Assert.Single(Differences(recorded, snapshot.ToJsonString())));
    }

    private static GeneratedAnswer Answer(IReadOnlyDictionary<string, ClassifiedFate>? classified, string? reason, LibraryModel? model = null) =>
        new(2, "M:Lib.C.M(System.Action)", new GenerationAssembly("Lib", "1.0", null), classified, reason, model, null,
            new GenerationRecord(null, null, [], 0, [], new Dictionary<string, string>(), [], 0, [],
                                 new Dictionary<string, IReadOnlyList<string>>(), 0, 0));

    /// <summary>Checks that recording refuses a lower floor before touching bytes, and accepts an equal or higher floor.</summary>
    /// <param name="property">The count property.</param>
    /// <param name="group">The LINQ assembly group, or null for a top-level count.</param>
    [Theory]
    [InlineData("overridable", true)]
    [InlineData("driver-not-synthesized", false)]
    public void Recording_lowers_a_floor_only_by_answers_now_refused_as_overridable(string reason, bool recorded)
    {
        // Component.set_Site: a model is applied by the member a call names, so a member a call may dispatch away from loses its
        // exact answer for that reason alone.
        var path = Path.Combine(Path.GetTempPath(), $"ch-floor-{Guid.NewGuid():N}.json");
        const string BEFORE = "{\"accessors\":[{\"member\":\"M:A.set_Site\",\"reason\":null,\"model\":{}}],\"exact\":0,\"fateGoldEntries\":0," +
                              "\"effectsExact\":0,\"linqExact\":{},\"accessorsExact\":1}";
        File.WriteAllText(path, BEFORE);
        try
        {
            var after = $"{{\"accessors\":[{{\"member\":\"M:A.set_Site\",\"reason\":\"{reason}\",\"model\":null}}],\"exact\":0,\"fateGoldEntries\":0," +
                        "\"effectsExact\":0,\"linqExact\":{},\"accessorsExact\":0}";
            if (recorded)
                RecordSnapshot(path, after);
            else
                Assert.Throws<InvalidOperationException>(() => RecordSnapshot(path, after));
            Assert.Equal(recorded ? after : BEFORE, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static void AssertRecordingPreservesFloor(string property, string? group = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ch-floor-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, RECORDING_SNAPSHOT);
        try
        {
            var current = System.Text.Json.Nodes.JsonNode.Parse(RECORDING_SNAPSHOT)!;
            var counts = group is null ? current : current[property]!;
            var key = group ?? property;
            var floor = (int)counts[key]!;
            counts[key] = floor - 1;
            var error = Assert.Throws<InvalidOperationException>(() => RecordSnapshot(path, current.ToJsonString()));
            Assert.Contains(key, error.Message);
            Assert.Equal(RECORDING_SNAPSHOT, File.ReadAllText(path));
            foreach (var count in new[] { floor, floor + 1 })
            {
                counts[key] = count;
                RecordSnapshot(path, current.ToJsonString());
                Assert.Equal(current.ToJsonString(), File.ReadAllText(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [RequiresModelEvalsFact]
    public void Answers_match_the_recorded_snapshot()
    {
        var fresh = Snapshot(InstalledRun, EffectEvals.InstalledRun, LinqEvals.InstalledRun, AccessorEvals.InstalledRun);
        if (Environment.GetEnvironmentVariable(RECORD_VARIABLE) == "1")
        {
            RecordSnapshot(SnapshotPath, fresh);
            return;
        }

        Assert.True(File.Exists(SnapshotPath), $"{SnapshotPath} is missing; record it with CH_MODEL_EVALS=1 {RECORD_VARIABLE}=1.");
        var differences = Differences(File.ReadAllText(SnapshotPath), fresh);
        Assert.True(differences.Count == 0, "The model evals differ from generator-snapshot.json:\n" + string.Join("\n", differences));
    }

    [RequiresModelEvalsFact]
    public void Every_package_records_its_compiled_bodies_and_drivers()
    {
        var run = InstalledRun;

        Assert.Equal(Inputs.Keys.Order(StringComparer.Ordinal), run.Packages.Select(package => package.Assembly));
        foreach (var package in run.Packages)
        {
            var answers = run.Answers.Where(answer => answer.Gold.Assembly == package.Assembly).ToArray();
            Assert.Equal(Inputs[package.Assembly].Package, package.Package);
            Assert.True(package.Bodies > 0, $"{package.Assembly}: no method bodies");
            Assert.InRange(package.ExternBodies, 0, package.Bodies);
            Assert.Equal(answers.Length, package.Members);
            Assert.Equal(answers.Count(answer => answer.DriverSynthesized), package.DriversSynthesized);
            Assert.All(answers, answer => Assert.Equal(package.ImplementationVersion, ImplementationVersionOf(answer)));
        }

        Assert.Equal([10, 1, 5, 2], new[] { "System.Linq", "System.Security.Claims", "Polly", "Google.Protobuf" }
                                       .Select(assembly => run.Packages.Single(package => package.Assembly == assembly).Members));
        Assert.Equal("7.2.3", run.Packages.Single(package => package.Assembly == "Polly").ImplementationVersion);
        Assert.Equal("3.21.9", run.Packages.Single(package => package.Assembly == "Google.Protobuf").ImplementationVersion);
        Assert.StartsWith("8.0.", run.Packages.Single(package => package.Assembly == "System.Linq").ImplementationVersion);
    }

    [RequiresModelEvalsFact]
    public void The_compile_fix_makes_no_compiled_body_extern()
    {
        // Every package of the evals compiled directly, past the cache, without the repair of the decompiler's artefacts and with it: the
        // repair makes no body extern, and a library refused without it is refused for no CS0104 or CS0051 with it.
        var resolver = ImplementationAssemblies.ForThisProcess();
        var inputs = Inputs.Values.Concat(EffectEvals.ReadGold().Select(member => member.Input))
                           .Concat(LinqEvals.ReadGold().Select(member => member.Input))
                           .Concat(AccessorEvals.ReadGold().Select(member => member.Input))
                           .Distinct();
        var assemblies = inputs.Select(input => resolver.Resolve(input.Assembly, input.Version, input.Package, input.Framework))
                               .Where(resolution => resolution is { Reason: null, Assembly: not null })
                               .Select(resolution => resolution.Assembly!)
                               .DistinctBy(assembly => assembly.Path, StringComparer.OrdinalIgnoreCase)
                               .ToArray();

        Assert.Contains(assemblies, assembly => assembly.AssemblyName == "AutoMapper");
        foreach (var assembly in assemblies)
        {
            var name = $"{assembly.AssemblyName} {assembly.ImplementationVersion}";
            var module = LibraryCompilation.Decompile(assembly, CancellationToken.None);
            var references = assembly.References.Select(reference => (MetadataReference)MetadataReference.CreateFromFile(reference)).ToArray();
            var kept = LibraryCompilation.CompileModule(assembly.AssemblyName, module, references, CancellationToken.None, repairArtefacts: false);
            var repaired = LibraryCompilation.CompileModule(assembly.AssemblyName, module, references, CancellationToken.None);

            if (kept.Reason is null)
            {
                Assert.True(repaired.Reason is null, $"{name}: {repaired.Reason}: {string.Join(", ", repaired.Errors)}");
                var added = repaired.ExternMembers.Except(kept.ExternMembers, StringComparer.Ordinal).ToArray();
                Assert.True(added.Length == 0, $"{name}: the repair makes extern {string.Join(", ", added)}");
            }
            else
            {
                Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, kept.Reason);
                Assert.True(!repaired.Errors.Contains("CS0104") && !repaired.Errors.Contains("CS0051"), $"{name}: {string.Join(", ", repaired.Errors)}");
            }
        }
    }

    [RequiresModelEvalsFact]
    public void Missing_input_fails_under_the_flag()
    {
        using var install = new GenerationInstall();
        var noFramework = Assert.Throws<MissingEvalInputException>(() => Run(install.Resolver(), GoldPath));
        Assert.Contains("System.Linq", noFramework.Message);

        install.SharedFramework("8.0.31", ["System.Linq", "System.Security.Claims"]);
        var noPackage = Assert.Throws<MissingEvalInputException>(() => Run(install.Resolver(), GoldPath));
        Assert.Contains("Polly", noPackage.Message);

        var noGold = Assert.Throws<MissingEvalInputException>(() => Run(install.Resolver(), Path.Combine(install.Root, "gold.json")));
        Assert.Contains("gold.json", noGold.Message);
    }

    [RequiresModelEvalsFact]
    public void Message_parser_constructor_finishes_under_the_bound()
    {
        // The constructor's trigger actions reach Protobuf's JSON parser; with the triggers no root of the fate run (A1), and a holder's
        // confirmation run under the fate run's bound that collects accesses only in the driver's actions (A5, ADR 0025), the driver
        // gets its answer — a fate, unknown-execution from the confirmation run, or closure-bound — in under a minute.
        var input = Inputs["Google.Protobuf"];
        var request = new GenerationRequest(input.Assembly, input.Version, "M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0})", input.Package,
                                            input.Framework);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var answer = ModelGenerator.Generate(request, ImplementationAssemblies.ForThisProcess(), timeout.Token);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"{clock.Elapsed.TotalSeconds:0.0} s");
        Assert.True(answer.Classified is { Count: > 0 } || answer.Reason == GenerationReasons.CLOSURE_BOUND, $"{answer.Reason}: {answer.Detail}");
    }

    [RequiresModelEvalsTheory]
    [InlineData("3.21.9")]
    [InlineData("3.30.2")]
    public void Message_parser_holder_is_covered_and_its_confirmation_passes_the_bound(string version)
    {
        // Both ParseFrom(ReadOnlySpan<byte>) are triggers, their span built from a seeded array, and object's own members are none, the
        // holder being the constructor's result: the holder is covered. Its confirmation run passes the fate run's bound, and past it the
        // factory's probe fires in the library's type initializers too (ADR 0025): the factory stays unknown-execution.
        var input = Inputs["Google.Protobuf"];
        var request = new GenerationRequest(input.Assembly, version, "M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0})", input.Package,
                                            input.Framework);

        var trace = ModelGenerator.Trace(request, ImplementationAssemblies.ForThisProcess(), CancellationToken.None);

        Assert.True(trace.Driver is not null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        Assert.Empty(trace.Driver.Uncovered[FateClassifier.RESULT]);
        var triggers = trace.Driver.Triggers.Select(trigger => trigger.Member).ToArray();
        Assert.Equal(22, triggers.Length);
        Assert.Contains("M:Google.Protobuf.MessageParser`1.ParseFrom(System.ReadOnlySpan{System.Byte})", triggers);
        Assert.Contains("M:Google.Protobuf.MessageParser.ParseFrom(System.ReadOnlySpan{System.Byte})", triggers);
        Assert.DoesNotContain(triggers, trigger => trigger.StartsWith("M:System.Object.", StringComparison.Ordinal));
        Assert.True(trace.Confirmation is { Stopped: true }, "the confirmation run did not run or was not stopped");
        Assert.True(trace.Confirmation.StoppedAtReachableBodies > 1500);
        Assert.Equal(new ClassifiedFate(FateClassifier.UNKNOWN_EXECUTION, null), trace.Answer.Classified?["factory"]);
    }

    [RequiresModelEvalsFact]
    public void EF_Core_members_that_overflowed_get_an_answer()
    {
        // The census members whose driver nested one seed class inside the next down EF Core's metadata types, a new type each turn,
        // until the stack overflowed (R7): each now gets an answer, and none is a missing input.
        var entityFramework = new EvalInput("Microsoft.EntityFrameworkCore", "8.0.0", "Microsoft.EntityFrameworkCore", "net8.0");
        var identity = new EvalInput("Microsoft.AspNetCore.Identity.EntityFrameworkCore", "8.0.0", "Microsoft.AspNetCore.Identity.EntityFrameworkCore", "net8.0");
        (EvalInput Input, string Member)[] members =
        [
            (entityFramework, "M:Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker.set_QueryTrackingBehavior(Microsoft.EntityFrameworkCore.QueryTrackingBehavior)"),
            (entityFramework, "M:Microsoft.EntityFrameworkCore.DbContextOptionsBuilder`1.#ctor"),
            (entityFramework, "M:Microsoft.EntityFrameworkCore.DbContextOptionsBuilder`1.get_Options"),
            (entityFramework, "M:Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddDbContext``1(" +
                              "Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Microsoft.EntityFrameworkCore.DbContextOptionsBuilder}," +
                              "Microsoft.Extensions.DependencyInjection.ServiceLifetime,Microsoft.Extensions.DependencyInjection.ServiceLifetime)"),
            (identity, "M:Microsoft.Extensions.DependencyInjection.IdentityEntityFrameworkBuilderExtensions.AddEntityFrameworkStores``1(" +
                       "Microsoft.AspNetCore.Identity.IdentityBuilder)")
        ];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        foreach (var (input, member) in members)
        {
            var answer = ModelGenerator.Generate(new GenerationRequest(input.Assembly, input.Version, member, input.Package, input.Framework),
                                                 ImplementationAssemblies.ForThisProcess(), timeout.Token);

            Assert.False(answer.Reason is GenerationReasons.NO_IMPLEMENTATION or GenerationReasons.MEMBER_NOT_FOUND, $"{member}: {answer.Reason}: {answer.Detail}");
            Assert.True(answer.Classified is not null || answer.Reason is not null, $"{member}: neither classified nor refused");
        }
    }

    [RequiresModelEvalsFact]
    public void Serilog_configuration_getter_stops_at_its_limit()
    {
        // The census getters of Serilog's LoggerConfiguration ran past 600 s inside the access collection, whose loops checked no token: the
        // generation now stops within seconds of its limit, as the answer it gives or as the cancellation of the limit.
        var serilog = new EvalInput("Serilog", "2.12.0", "Serilog", "net8.0");
        var resolver = ImplementationAssemblies.ForThisProcess();
        var resolution = resolver.Resolve(serilog.Assembly, serilog.Version, serilog.Package, serilog.Framework);
        if (resolution.Reason == GenerationReasons.NO_IMPLEMENTATION)
            throw new MissingEvalInputException(resolution.Detail);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = Record.Exception(() => ModelGenerator.Generate(
            new GenerationRequest(serilog.Assembly, serilog.Version, "M:Serilog.LoggerConfiguration.get_WriteTo", serilog.Package, serilog.Framework),
            resolver, limit.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"{clock.Elapsed.TotalSeconds:0.0} s against a limit of 10 s");
        if (error is not null)
            Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    private static string ImplementationVersionOf(EvalAnswer answer)
    {
        var input = Inputs[answer.Gold.Assembly];
        return input.Package is null
            ? Path.GetFileName(Path.GetDirectoryName(answer.Answer.Generation.Implementation!.Path))!
            : input.Version;
    }

    internal static int RecordedCount(string property)
    {
        Assert.True(File.Exists(SnapshotPath), $"{SnapshotPath} is missing; record it with CH_MODEL_EVALS=1 {RECORD_VARIABLE}=1.");
        return (int?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SnapshotPath))![property]
               ?? throw new Xunit.Sdk.XunitException($"generator-snapshot.json has no {property}");
    }

    internal static int RecordedLinqExact(string group)
    {
        Assert.True(File.Exists(SnapshotPath), $"{SnapshotPath} is missing; record it with CH_MODEL_EVALS=1 {RECORD_VARIABLE}=1.");
        return (int?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SnapshotPath))!["linqExact"]?[group]
               ?? throw new Xunit.Sdk.XunitException($"generator-snapshot.json has no linqExact.{group}");
    }

    private static (IMethodSymbol Member, Compilation Compilation) DeclaringMember(EvalAnswer answer)
    {
        var input = Inputs[answer.Gold.Assembly];
        var resolution = ImplementationAssemblies.ForThisProcess().Resolve(input.Assembly, input.Version, input.Package, input.Framework);
        var library = LibraryCompilation.Compile(resolution.Assembly!, CancellationToken.None);
        var compilation = Assert.IsType<Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(library.Compilation);
        return (Assert.IsAssignableFrom<IMethodSymbol>(DriverSynthesizer.FindMember(compilation, answer.Gold.Id)), compilation);
    }

    private static IReadOnlyList<Score> Scored(EvalRun run) =>
        run.Answers.SelectMany(answer => answer.Gold.DelegateParams.Select(parameter =>
                                   new Score(answer.Gold.Id, parameter, answer.Gold.Fate, Answered(answer.Answer, parameter), answer.Answer.Reason)))
           .ToArray();

    /// <summary>One gold parameter's fate and the answer's.</summary>
    /// <param name="Member">The member id.</param>
    /// <param name="Parameter">The parameter.</param>
    /// <param name="Gold">The gold fate.</param>
    /// <param name="Answered">The answered fate.</param>
    /// <param name="Reason">The answer's reason when it classified nothing.</param>
    private sealed record Score(string Member, string Parameter, ClassifiedFate Gold, ClassifiedFate Answered, string? Reason);
}

/// <summary>The model evals' collection: not run in parallel with any other test.</summary>
[CollectionDefinition(ModelEvalTests.COLLECTION, DisableParallelization = true)]
public sealed class ModelEvalCollection;
