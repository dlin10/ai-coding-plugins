using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using GenerationCauses = ConcurrencyHunter.Analysis.GenerationCauses;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>What a scope's coverage says about the generated layer (R5): the calls a generated model describes, counted beside the calls the
/// other layers describe, and the generation targets of the scope that no generated model describes, once each, each with the cause that
/// says why and in the order the report and the measurement keep them.</summary>
public sealed class GeneratedCoverageTests : IDisposable
{
    private const string PACKAGE = "Acme.Lib";
    private const string LIBRARY = """
        namespace Acme
        {
            public static class Api
            {
                public static void Touch(object value) { }
                public static int Compute(int value) => value;
            }
        }
        """;

    private const string DEPENDENT_LIBRARY = "namespace Acme { public static class Api { public static void Fill(Dep.Item item, Dep.Base[] bag) { } } }";
    private const string NO_CALLS = "public static class App { }";
    private const string TOUCH_ONCE = "public static class ModelDriver { public static void V_Call() { Acme.Api.Touch(new object()); } }";
    private const string TOUCH_TWICE = "public static class ModelDriver { public static void V_Call() { Acme.Api.Touch(new object()); Acme.Api.Touch(new object()); } }";

    private const string THREE_LAYERS = """
        public static class ModelDriver
        {
            public static void V_Call()
            {
                Acme.Api.Touch(new object());
                Acme.Api.Compute(1);
                System.Array.Reverse((System.Array)new int[1]);
            }
        }
        """;

    private const string LEDGER = """
        public sealed class Ledger { public int Total; public void Add(int amount) => Total += amount; }
        public class LedgerController(Ledger ledger) : ControllerBase
        {
            public void Post(int amount) => ledger.Add(amount);
            public int Get() => ledger.Total;
        }
        """;

    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    private string Repository => Path.Combine(_install.Root, "repository");

    /// <summary>What a situation of one generation target in one compilation is, for <see cref="Refusal_cause_follows_mode_and_outcome"/>.</summary>
    public enum Situation
    {
        StoredModel,
        StoredModelNotApplicable,
        StoredRefusal,
        StoredTimeout,
        StoredRefusalOfUnavailableTarget,
        UnavailableWithoutAnswer,
        RunModelNotKept,
        NoAnswer,
        GeneratedModel,
        GeneratedModelNotApplicable,
        GeneratedRefusal,
        GeneratedRefusalWithoutReason,
        GeneratedTimeout,
        GeneratedBudget,
        GeneratedFailed,
        GeneratedNoImplementation
    }

    /// <summary>How a model applies in the two compilations of a scope.</summary>
    public enum Applicability { Both, FirstOnly, Neither }

    /// <summary>Where the answer for a model comes from.</summary>
    public enum Answer { Stored, NewlyGenerated }

    // R5's table, written from the requirement: the cause of a situation under Generate and under ReadOnly; nothing under None.
    private static readonly IReadOnlyDictionary<Situation, (string? Generate, string? ReadOnly)> Causes = new Dictionary<Situation, (string?, string?)>
    {
        [Situation.StoredModel] = (null, null),
        [Situation.StoredModelNotApplicable] = ("not-applicable", "not-applicable"),
        [Situation.StoredRefusal] = ("unknown-touch", "unknown-touch"),
        [Situation.StoredTimeout] = ("timeout", "timeout"),
        // A stored answer settles the member whether or not its implementation is on disk.
        [Situation.StoredRefusalOfUnavailableTarget] = ("unknown-touch", "unknown-touch"),
        [Situation.UnavailableWithoutAnswer] = ("no-implementation", "no-implementation"),
        [Situation.RunModelNotKept] = (null, "not-generated"),
        [Situation.NoAnswer] = ("budget", "not-generated"),
        // A read-only run generates nothing, so whatever a generation would have answered is not its answer.
        [Situation.GeneratedModel] = (null, "not-generated"),
        [Situation.GeneratedModelNotApplicable] = ("not-applicable", "not-generated"),
        [Situation.GeneratedRefusal] = ("driver-not-synthesized", "not-generated"),
        [Situation.GeneratedRefusalWithoutReason] = ("unseeded-read", "not-generated"),
        [Situation.GeneratedTimeout] = ("timeout", "not-generated"),
        [Situation.GeneratedBudget] = ("budget", "not-generated"),
        [Situation.GeneratedFailed] = ("failed", "not-generated"),
        [Situation.GeneratedNoImplementation] = ("no-implementation", "not-generated")
    };

    public static TheoryData<ModelGeneration, Situation, string?> CauseCases()
    {
        var cases = new TheoryData<ModelGeneration, Situation, string?>();
        foreach (var (situation, (generate, readOnly)) in Causes)
        {
            cases.Add(ModelGeneration.Generate, situation, generate);
            cases.Add(ModelGeneration.ReadOnly, situation, readOnly);
            cases.Add(ModelGeneration.None, situation, null);
        }

        return cases;
    }

    public static TheoryData<ModelGeneration, Answer, Applicability, bool> MixedApplicabilityCases()
    {
        var cases = new TheoryData<ModelGeneration, Answer, Applicability, bool>();
        foreach (var (mode, answer) in new[] { (ModelGeneration.Generate, Answer.Stored), (ModelGeneration.Generate, Answer.NewlyGenerated),
                                               (ModelGeneration.ReadOnly, Answer.Stored) })
        {
            foreach (var applicability in Enum.GetValues<Applicability>())
            {
                cases.Add(mode, answer, applicability, true);
                cases.Add(mode, answer, applicability, false);
            }
        }

        return cases;
    }

    [Fact]
    public void Known_call_generated_counts_a_generated_entry()
    {
        _install.SharedFramework("8.0.5");
        var image = Library("one");
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, image);
        var project = Project(DriverSynthesizer.ASSEMBLY, "1.0.0", "net8.0", image, TOUCH_ONCE);
        var touch = Member(project, "Touch");
        var target = TargetOf(project, touch);
        var table = Table((project, touch, target.Key));
        Assert.Empty(table.AddFile(Stored(target.Key, GeneratedModelAnswer.OfModel(Model(touch)))));

        var without = Counters(project, LibraryModels.BuiltIn);
        var with = Counters(project, LibraryModels.BuiltIn.WithGenerated(table));

        Assert.Equal((0, 1), (without[CoverageCounters.KNOWN_CALL_GENERATED], with[CoverageCounters.KNOWN_CALL_GENERATED]));
        Assert.Equal(without[CoverageCounters.OPAQUE_CALL] - 1, with[CoverageCounters.OPAQUE_CALL]);
        Assert.Equal(without[CoverageCounters.KNOWN_CALL] + 1, with[CoverageCounters.KNOWN_CALL]);
        // The run's own counters are filled by whoever runs the scope: a scope's collection leaves them at zero, and has them.
        Assert.All(new[] { without, with }, counters => Assert.Equal((0, 0), (counters[CoverageCounters.GENERATED_IN_RUN],
                                                                             counters[CoverageCounters.PAIRS_REMOVED_GENERATED])));
        // A call a generated model describes is no member without one, where the layer has no answer it is.
        Assert.Empty(GenerationCauses.Items(ModelGeneration.ReadOnly, [Reached(table, project, touch, target)]));
        var empty = Table((project, touch, target.Key));
        var item = Assert.Single(GenerationCauses.Items(ModelGeneration.ReadOnly, [Reached(empty, project, touch, target)]));
        Assert.Equal((Id(touch), target.Key.Text, GenerationCauses.NOT_GENERATED), (item.MemberId, item.FileKey, item.Cause));
    }

    [Fact]
    public void Two_calls_of_one_generated_target_are_two_known_calls()
    {
        _install.SharedFramework("8.0.5");
        var image = Library("two");
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, image);
        var project = Project(DriverSynthesizer.ASSEMBLY, "1.0.0", "net8.0", image, TOUCH_TWICE);
        var touch = Member(project, "Touch");
        var target = TargetOf(project, touch);
        var table = Table((project, touch, target.Key));
        Assert.Empty(table.AddFile(Stored(target.Key, GeneratedModelAnswer.OfModel(Model(touch)))));

        var without = Counters(project, LibraryModels.BuiltIn);
        var with = Counters(project, LibraryModels.BuiltIn.WithGenerated(table));

        // The counter counts calls, as the other layers' do: one target reached twice is two known calls.
        Assert.Equal((0, 2), (without[CoverageCounters.KNOWN_CALL_GENERATED], with[CoverageCounters.KNOWN_CALL_GENERATED]));
        Assert.Equal(without[CoverageCounters.OPAQUE_CALL] - 2, with[CoverageCounters.OPAQUE_CALL]);
        Assert.Equal(without[CoverageCounters.KNOWN_CALL] + 2, with[CoverageCounters.KNOWN_CALL]);
        // The list counts targets: the same two calls without an answer are one member without a generated model.
        var empty = Table((project, touch, target.Key));
        var call = Reached(empty, project, touch, target);
        var item = Assert.Single(GenerationCauses.Items(ModelGeneration.ReadOnly, [call, call]));
        Assert.Equal((Id(touch), GenerationCauses.NOT_GENERATED), (item.MemberId, item.Cause));
    }

    [Fact]
    public void Known_call_is_the_sum_of_three_layers()
    {
        _install.SharedFramework("8.0.5");
        var image = Library("three");
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, image);
        var project = Project(DriverSynthesizer.ASSEMBLY, "1.0.0", "net8.0", image, THREE_LAYERS);
        var (touch, compute) = (Member(project, "Touch"), Member(project, "Compute"));
        var target = TargetOf(project, touch);
        var table = Table((project, touch, target.Key));
        Assert.Empty(table.AddFile(Stored(target.Key, GeneratedModelAnswer.OfModel(Model(touch)))));
        // A project entry for `Compute`; `Array.Reverse(Array)` is the built-in layer's, `Touch` the generated layer's.
        var projectEntry = new LibraryModel(DocumentationCommentId.CreateDeclarationId(compute)!,
                                            [new SupportedAssemblyVersion(PACKAGE, new Version(0, 0, 0, 0), new Version(9, 0, 0, 0))], [],
                                            ModelLayer.Project, false, compute.ContainingAssembly.Identity.Version);

        var builtInOnly = Counters(project, LibraryModels.BuiltIn);
        var withProject = Counters(project, LibraryModels.WithProject([projectEntry]));
        var withAll = Counters(project, LibraryModels.WithProject([projectEntry]).WithGenerated(table));

        var layers = new[] { CoverageCounters.KNOWN_CALL_BUILT_IN, CoverageCounters.KNOWN_CALL_PROJECT, CoverageCounters.KNOWN_CALL_GENERATED };
        var builtIn = builtInOnly[CoverageCounters.KNOWN_CALL_BUILT_IN];
        // `Array.Reverse(Array)` is the built-in layer's, and each of the other two layers takes one more call from the opaque ones.
        Assert.True(builtIn > 0);
        Assert.Equal([builtIn, 0, 0], layers.Select(layer => builtInOnly[layer]));
        Assert.Equal([builtIn, 1, 0], layers.Select(layer => withProject[layer]));
        Assert.Equal([builtIn, 1, 1], layers.Select(layer => withAll[layer]));
        Assert.All(new[] { builtInOnly, withProject, withAll },
                   counters => Assert.Equal(layers.Sum(layer => counters[layer]), counters[CoverageCounters.KNOWN_CALL]));
        Assert.Equal(builtInOnly[CoverageCounters.OPAQUE_CALL] - 2, withAll[CoverageCounters.OPAQUE_CALL]);
    }

    [Fact]
    public void Refusal_list_is_ordered_and_unique()
    {
        _install.SharedFramework("8.0.5");
        // One assembly, two package versions: two file keys, in the order of their text.
        var (older, newer) = (Library("older"), Library("newer"));
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, older);
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.1", "net8.0", PACKAGE, newer);
        var (projectOlder, projectNewer) = (Project("Older", "1.0.0", "net8.0", older), Project("Newer", "1.0.1", "net8.0", newer));
        var (touchOlder, computeOlder, touchNewer, computeNewer) = (Member(projectOlder, "Touch"), Member(projectOlder, "Compute"),
                                                                    Member(projectNewer, "Touch"), Member(projectNewer, "Compute"));
        var (keyOlder, keyNewer) = (TargetOf(projectOlder, touchOlder).Key, TargetOf(projectNewer, touchNewer).Key);
        Assert.True(string.CompareOrdinal(keyOlder.Text, keyNewer.Text) < 0);
        // The newer asset's `Compute` has a model; nothing else has an answer.
        var table = Table((projectOlder, touchOlder, keyOlder), (projectNewer, touchNewer, keyNewer));
        Assert.Empty(table.AddFile(Stored(keyNewer, GeneratedModelAnswer.OfModel(Model(computeNewer)))));
        var calls = new[]
        {
            Reached(table, projectNewer, touchNewer), Reached(table, projectOlder, touchOlder), Reached(table, projectOlder, computeOlder),
            Reached(table, projectNewer, touchNewer), Reached(table, projectNewer, computeNewer), Reached(table, projectOlder, computeOlder)
        };

        foreach (var order in new[] { calls, [.. Enumerable.Reverse(calls)], [.. calls.OrderBy(call => call.Target.Key.Text, StringComparer.Ordinal)] })
        {
            var items = GenerationCauses.Items(ModelGeneration.ReadOnly, order);

            // In ordinal order of declaration id and then of file key, each target once however many calls reach it, and the target
            // with a model not at all.
            Assert.Equal([(Id(computeOlder), keyOlder.Text, "Acme.Api.Compute(int)"), (Id(touchOlder), keyOlder.Text, "Acme.Api.Touch(object)"),
                          (Id(touchNewer), keyNewer.Text, "Acme.Api.Touch(object)")],
                         items.Select(item => (item.MemberId, item.FileKey, item.Member)));
            Assert.All(items, item => Assert.Equal(GenerationCauses.NOT_GENERATED, item.Cause));
        }

        // Where the generated layer is off nothing is listed.
        Assert.Empty(GenerationCauses.Items(ModelGeneration.None, calls));
    }

    [Fact]
    public void One_member_through_two_file_keys_is_two_targets()
    {
        _install.SharedFramework("8.0.5");
        var (older, newer) = (Library("older"), Library("newer"));
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, older);
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.1", "net8.0", PACKAGE, newer);
        var (projectOlder, projectNewer) = (Project("Older", "1.0.0", "net8.0", older), Project("Newer", "1.0.1", "net8.0", newer));
        var (touchOlder, touchNewer) = (Member(projectOlder, "Touch"), Member(projectNewer, "Touch"));
        var (keyOlder, keyNewer) = (TargetOf(projectOlder, touchOlder).Key, TargetOf(projectNewer, touchNewer).Key);
        Assert.Equal(Id(touchOlder), Id(touchNewer));
        Assert.NotEqual(keyOlder, keyNewer);
        var refusal = new GeneratedRefusal(ModelReasons.UNKNOWN_TOUCH, "a probe was handed to a call the analysis does not follow", ["probe-handed-to-unseen"]);
        var (fileOlder, fileNewer) = (Stored(keyOlder, GeneratedModelAnswer.OfModel(Model(touchOlder))),
                                      Stored(keyNewer, GeneratedModelAnswer.OfRefusal(Id(touchNewer), refusal)));
        var table = Table((projectOlder, touchOlder, keyOlder), (projectNewer, touchNewer, keyNewer));
        Assert.Empty(table.AddFile(fileOlder).Concat(table.AddFile(fileNewer)));
        var models = LibraryModels.BuiltIn.WithGenerated(table);

        // The model is applied where its file is the call's, and the refusal of the other file leaves its call opaque.
        Assert.Equal(ModelLayer.Generated, models.Find(touchOlder, projectOlder)!.Layer);
        Assert.Null(models.Find(touchNewer, projectNewer));
        var calls = new[] { Reached(table, projectOlder, touchOlder), Reached(table, projectNewer, touchNewer) };
        foreach (var mode in new[] { ModelGeneration.Generate, ModelGeneration.ReadOnly })
        {
            // Two targets, one listed: the refusal once, under its own file's key and its own reason.
            var item = Assert.Single(GenerationCauses.Items(mode, calls));
            Assert.Equal((Id(touchNewer), keyNewer.Text, ModelReasons.UNKNOWN_TOUCH), (item.MemberId, item.FileKey, item.Cause));
        }

        // The same member refused by both files is two items, each with the answer of its own file, in the order of the keys.
        var both = Table((projectOlder, touchOlder, keyOlder), (projectNewer, touchNewer, keyNewer));
        var other = new GeneratedRefusal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, "no driver", []);
        Assert.Empty(both.AddFile(Stored(keyOlder, GeneratedModelAnswer.OfRefusal(Id(touchOlder), other)))
                         .Concat(both.AddFile(fileNewer)));
        var items = GenerationCauses.Items(ModelGeneration.ReadOnly, [Reached(both, projectNewer, touchNewer), Reached(both, projectOlder, touchOlder)]);
        Assert.Equal([(keyOlder.Text, GenerationReasons.DRIVER_NOT_SYNTHESIZED), (keyNewer.Text, ModelReasons.UNKNOWN_TOUCH)],
                     items.Select(listed => (listed.FileKey, listed.Cause)));
    }

    [Theory]
    [MemberData(nameof(CauseCases))]
    public void Refusal_cause_follows_mode_and_outcome(ModelGeneration mode, Situation situation, string? expected)
    {
        Assert.Equal(expected, GenerationCauses.Of(mode, Standing(situation)));
    }

    [Fact]
    public void Unavailable_targets_of_one_package_version_are_one_item()
    {
        // The packages are not on disk: no asset names an MVID, so the key is the package's id and version alone.
        _install.SharedFramework("6.0.5");
        _install.SharedFramework("8.0.5");
        var image = Library("missing");
        var (net8, net6, other) = (Project("Net8", "1.0.0", "net8.0", image), Project("Net6", "1.0.0", "net6.0", image, packMajor: 6),
                                   Project("Other", "1.0.1", "net8.0", image));
        var (touch8, touch6, compute8, touchOther) = (Member(net8, "Touch"), Member(net6, "Touch"), Member(net8, "Compute"), Member(other, "Touch"));
        var targets = new[] { (net8, touch8), (net6, touch6), (net8, compute8), (other, touchOther) }
            .Select(call => (call.Item1, call.Item2, Target: Assert.IsAssignableFrom<IdentifiedGenerationTarget>(
                                 GenerationTargets.Of(call.Item2, call.Item1, _install.Resolver(), CancellationToken.None))))
            .ToArray();
        Assert.All(targets, call => Assert.IsType<UnavailableGenerationTarget>(call.Target));
        Assert.Equal(targets[0].Target.Key, targets[1].Target.Key);
        Assert.Null(targets[0].Target.Key.Mvid);
        var calls = targets.Select(call => new ReachedTarget(call.Item2, call.Target, new TargetStanding(StoredGeneratedAnswer.Absent, true))).ToArray();

        foreach (var mode in new[] { ModelGeneration.Generate, ModelGeneration.ReadOnly })
        {
            var items = GenerationCauses.Items(mode, calls);

            // Two compilations of one package version reach `Touch`: one item. `Compute` is another target of the version, and the
            // other package version is another key. The package is named as its folder names it, which is all a missing package has.
            Assert.Equal([(Id(compute8), "acme.lib/1.0.0"), (Id(touch8), "acme.lib/1.0.0"), (Id(touch8), "acme.lib/1.0.1")],
                         items.Select(item => (item.MemberId, item.FileKey)));
            Assert.All(items, item => Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, item.Cause));
        }

        Assert.Empty(GenerationCauses.Items(ModelGeneration.None, calls));
    }

    [Theory]
    [MemberData(nameof(MixedApplicabilityCases))]
    public void One_target_with_mixed_applicability_is_one_item(ModelGeneration mode, Answer answer, Applicability applicability, bool firstCompilationFirst)
    {
        _install.SharedFramework("8.0.5");
        // One package asset, so one file; two dependencies of one name and version in which the asset's Item is, and is not, a Base.
        var (inheriting, plain) = (Emit("Dep", "namespace Dep { public class Base { } public class Item : Base { } }"),
                                   Emit("Dep", "namespace Dep { public class Base { } public class Item { } }"));
        var asset = Emit(PACKAGE, DEPENDENT_LIBRARY, MetadataReference.CreateFromImage(inheriting));
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, asset);
        var (firstDependency, secondDependency) = applicability switch
        {
            Applicability.Both => (inheriting, inheriting),
            Applicability.FirstOnly => (inheriting, plain),
            _ => (plain, plain)
        };
        var (first, second) = (Project("First", "1.0.0", "net8.0", asset, dependency: firstDependency),
                               Project("Second", "1.0.0", "net8.0", asset, dependency: secondDependency));
        var (fillFirst, fillSecond) = (Member(first, "Fill"), Member(second, "Fill"));
        var target = TargetOf(first, fillFirst);
        Assert.Equal(target.Key, TargetOf(second, fillSecond).Key);
        // The model stores the item in the bag's cells, which converts only where Item derives from Base.
        var fill = new LibraryModel(fillFirst.GetDocumentationCommentId()!, [VersionRange(fillFirst)], [LibraryEffect.WriteCellsOf("bag")])
        {
            Stores = new Dictionary<string, IReadOnlyList<LibraryValue>> { ["bag"] = [new ArgumentValue("item")] }
        };
        var generated = GeneratedModelAnswer.OfModel(fill);
        var file = Stored(target.Key, generated);
        (Compilation Project, IMethodSymbol Member)[] order = firstCompilationFirst ? [(first, fillFirst), (second, fillSecond)]
                                                                                    : [(second, fillSecond), (first, fillFirst)];
        var table = Table(order.Select(call => (call.Project, call.Member, target.Key)).ToArray());
        var applying = applicability == Applicability.Both ? 2 : applicability == Applicability.FirstOnly ? 1 : 0;
        // A stored answer is the table's; one the run just generated is not in the table, and applies by the same check.
        var diagnostics = answer == Answer.Stored ? table.AddFile(file) : [];
        Assert.Equal(answer == Answer.Stored ? 2 - applying : 0, diagnostics.Count);
        var calls = order.Select(call => new ReachedTarget(call.Member, target, answer == Answer.Stored
                              ? new TargetStanding(table.Lookup(call.Project, call.Member, target.LookupId) ?? StoredGeneratedAnswer.Absent, false)
                              : new TargetStanding(StoredGeneratedAnswer.Absent, false, generated,
                                                   GeneratedModelFiles.Applies(file.Entries[fill.Id], call.Member, call.Project, out _)))).ToArray();

        var items = GenerationCauses.Items(mode, calls);

        if (applicability == Applicability.Both)
        {
            Assert.Empty(items);
            return;
        }

        // Applying in some compilations and not in others, or in none, is one item, whatever the order of the compilations.
        var item = Assert.Single(items);
        Assert.Equal((fill.Id, target.Key.Text, GenerationCauses.NOT_APPLICABLE), (item.MemberId, item.FileKey, item.Cause));
    }

    [Fact]
    public async Task Report_explains_the_generated_counters()
    {
        var lines = Render(await Analyze());

        Assert.Contains("  - generated-in-run 0: generation targets, a library member with the generated file that answers for it, whose model this run " +
                        "generated, in this scope or an earlier one, each once however many calls reach it; a member reached through two assets is two " +
                        "targets and counts twice", lines);
        Assert.Contains("  - pairs-removed-generated 0: candidate pairs the analysis without the generated models has and this one has not, a pair " +
                        "being the same two sites on the same resource however many executions meet there", lines);
        Assert.Contains("  - known-call-generated 0: known calls a generated library model describes", lines);
    }

    [Fact]
    public async Task Report_lists_the_members_without_a_generated_model()
    {
        var result = await Analyze();
        var scope = Assert.Single(result.Coverage);
        Assert.Contains("  - Members without a generated model: none", Render(result));

        var listed = result with
        {
            Coverage =
            [
                scope with
                {
                    GenerationRefusals =
                    [
                        new GenerationRefusal("Acme.Api.Compute(int)", "M:Acme.Api.Compute(System.Int32)", "Acme.Lib/1.0.0", "no-implementation"),
                        new GenerationRefusal("Acme.Api.Touch(object)", "M:Acme.Api.Touch(System.Object)", "Acme.Lib/1.0.0/5d4f0a1c-6e7b-4c3a-9a55-0b9f7d2e8c11.json",
                                              "unknown-touch")
                    ]
                }
            ]
        };

        var lines = Render(listed);
        var members = Assert.Single(lines, line => line.StartsWith("  - Members without a generated model: ", StringComparison.Ordinal));
        Assert.Equal("  - Members without a generated model: Acme.Api.Compute(int) (no-implementation, Acme.Lib/1.0.0); " +
                     "Acme.Api.Touch(object) (unknown-touch, Acme.Lib/1.0.0/5d4f0a1c-6e7b-4c3a-9a55-0b9f7d2e8c11.json)", members);
        // In the scope's coverage: after its counters, before the inventory.
        var index = Array.IndexOf(lines, members);
        Assert.True(Array.FindIndex(lines, other => other.StartsWith("  - wildcard-access ", StringComparison.Ordinal)) < index);
        Assert.StartsWith("  - Lowered but not reached", lines[index + 1], StringComparison.Ordinal);
    }

    /// <summary>What a situation comes to as a target's standing in one compilation.</summary>
    /// <param name="situation">The situation.</param>
    private static TargetStanding Standing(Situation situation)
    {
        var model = new LibraryModel("M:A.B.C", [SupportedAssemblyVersion.Framework("A")], []);
        StoredGeneratedAnswer Refused(string reason) => new(StoredGeneratedKind.Refusal, Refusal: new GeneratedRefusal(reason, "detail", ["a-cause"]));
        TargetStanding Generated(GeneratedModelAnswer answer, bool applies = false) => new(StoredGeneratedAnswer.Absent, false, answer, applies);
        GeneratedModelAnswer NewRefusal(string reason, params string[] causes) =>
            GeneratedModelAnswer.OfRefusal("M:A.B.C", new GeneratedRefusal(reason, "detail", causes));
        return situation switch
        {
            Situation.StoredModel => new TargetStanding(new StoredGeneratedAnswer(StoredGeneratedKind.Model, model), false),
            Situation.StoredModelNotApplicable => new TargetStanding(new StoredGeneratedAnswer(StoredGeneratedKind.ModelNotApplicable), false),
            Situation.StoredRefusal => new TargetStanding(Refused(ModelReasons.UNKNOWN_TOUCH), false),
            Situation.StoredTimeout => new TargetStanding(Refused(GenerationOutcome.TIMEOUT), false),
            Situation.StoredRefusalOfUnavailableTarget => new TargetStanding(Refused(ModelReasons.UNKNOWN_TOUCH), true),
            Situation.UnavailableWithoutAnswer => new TargetStanding(StoredGeneratedAnswer.Absent, true),
            Situation.RunModelNotKept => Generated(GeneratedModelAnswer.OfModel(model), applies: true),
            Situation.NoAnswer => new TargetStanding(StoredGeneratedAnswer.Absent, false),
            Situation.GeneratedModel => Generated(GeneratedModelAnswer.OfModel(model), applies: true),
            Situation.GeneratedModelNotApplicable => Generated(GeneratedModelAnswer.OfModel(model)),
            Situation.GeneratedRefusal => Generated(NewRefusal(GenerationReasons.DRIVER_NOT_SYNTHESIZED)),
            Situation.GeneratedRefusalWithoutReason => Generated(NewRefusal("", ModelCauses.UNSEEDED_READ, ModelCauses.PROBE_HANDED_TO_UNSEEN)),
            Situation.GeneratedTimeout => Generated(NewRefusal(GenerationOutcome.TIMEOUT)),
            Situation.GeneratedBudget => Generated(NewRefusal(GenerationOutcome.BUDGET)),
            Situation.GeneratedFailed => Generated(NewRefusal(GenerationOutcome.FAILED)),
            Situation.GeneratedNoImplementation => Generated(NewRefusal(GenerationReasons.NO_IMPLEMENTATION)),
            _ => throw new ArgumentOutOfRangeException(nameof(situation), situation, "Not a situation of the table.")
        };
    }

    /// <summary>The scope's analysis of the fixture, whose report the report tests read.</summary>
    private static Task<AnalysisResult> Analyze()
    {
        var solution = FixtureSolution.Create(("Case.cs", EngineFixture.Usings + LEDGER + EngineFixture.Startup("services.AddSingleton<Ledger>();")));
        return PhaseOneAnalyzer.AnalyzeAsync(solution, EngineFixture.ROOT_DIRECTORY, CancellationToken.None);
    }

    /// <summary>The lines of the report of an analysis.</summary>
    /// <param name="result">The analysis.</param>
    private static string[] Render(AnalysisResult result) => ReportRenderer.Render(ReportingTestData.CreateReport(result)).ReportMarkdown.Split('\n');

    /// <summary>The scope's coverage counters for a project analysed with some models.</summary>
    /// <param name="project">The project.</param>
    /// <param name="models">The models.</param>
    private static IReadOnlyDictionary<string, int> Counters(Compilation project, LibraryModels models) =>
        ScopePipeline.Run("scope:generated-coverage", [project], [], Path.GetTempPath(), new ProviderRegistry([new DriverRootProvider()]), models,
                          AnalysisLimits.Default, new(), null, CancellationToken.None).Collection!.Coverage.Counters;

    /// <summary>A generated layer that maps the declaring types of members to the keys of their files, with no file read yet.</summary>
    /// <param name="mapped">Each compilation, a member whose declaring type it maps, and the key of the type's file.</param>
    private static GeneratedModelTable Table(params (Compilation Compilation, IMethodSymbol Member, GeneratedFileKey Key)[] mapped)
    {
        var table = new GeneratedModelTable();
        foreach (var (compilation, member, key) in mapped)
            table.MapType(compilation, member.ContainingType, key);
        return table;
    }

    /// <summary>The call of a member as the scope's coverage meets it: its target and what the table says about it in the call's compilation.</summary>
    /// <param name="table">The scope's generated layer.</param>
    /// <param name="compilation">The call's compilation.</param>
    /// <param name="member">The called member.</param>
    private ReachedTarget Reached(GeneratedModelTable table, Compilation compilation, IMethodSymbol member) =>
        Reached(table, compilation, member, TargetOf(compilation, member));

    /// <summary>The call of a member as the scope's coverage meets it.</summary>
    /// <param name="table">The scope's generated layer.</param>
    /// <param name="compilation">The call's compilation.</param>
    /// <param name="member">The called member.</param>
    /// <param name="target">The member's target.</param>
    private static ReachedTarget Reached(GeneratedModelTable table, Compilation compilation, IMethodSymbol member, GenerationTarget target) =>
        new(member, target, new TargetStanding(table.Lookup(compilation, member, target.LookupId) ?? StoredGeneratedAnswer.Absent, false));

    /// <summary>The package's library, told apart from another asset by a comment: a different source is a different MVID.</summary>
    /// <param name="comment">What tells it apart.</param>
    private static byte[] Library(string comment) =>
        EmittedAssemblies.Image(PACKAGE, ReferenceScopes.VERSION_ATTRIBUTE + LIBRARY + "\n// " + comment);

    /// <summary>An assembly of version <c>1.0.0.0</c> compiled against the running runtime and the given references.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="source">Its source.</param>
    /// <param name="references">What it references besides the runtime.</param>
    private static byte[] Emit(string assemblyName, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(ReferenceScopes.VERSION_ATTRIBUTE + source)],
                                                   [.. EmittedAssemblies.RuntimeReferences, .. references],
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return stream.ToArray();
    }

    /// <summary>A project of a scope that references the package's asset under a framework's folder, as a project of that framework does.</summary>
    /// <param name="name">The project's assembly name.</param>
    /// <param name="version">The package version.</param>
    /// <param name="framework">The framework folder of the asset the project references.</param>
    /// <param name="image">The asset the project references.</param>
    /// <param name="source">The project's source.</param>
    /// <param name="dependency">A dependency of the package the project references, or <c>null</c> for none.</param>
    /// <param name="packMajor">The major of the project's <c>Microsoft.NETCore.App.Ref</c>.</param>
    private CSharpCompilation Project(string name, string version, string framework, byte[] image, string source = NO_CALLS, byte[]? dependency = null,
                                      int packMajor = 8)
    {
        var references = ReferenceScopes.Runtime(_install, packMajor)
                                        .Append(ReferenceScopes.Reference(image, ReferenceScopes.PackagePath(_install, PACKAGE, version, "lib", framework, PACKAGE)));
        if (dependency is not null)
            references = references.Append(ReferenceScopes.Reference(dependency, ReferenceScopes.Join(_install.Packages, "dep", "1.0.0", "lib", "net8.0", "Dep.dll")));
        return CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, path: Path.Combine(_install.Root, name + ".cs"))], references,
                                        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static string Id(IMethodSymbol member) => member.GetDocumentationCommentId()!;

    private static IMethodSymbol Member(Compilation compilation, string name) =>
        compilation.GetTypeByMetadataName("Acme.Api")!.GetMembers(name).OfType<IMethodSymbol>().Single();

    private GenerationTarget TargetOf(Compilation compilation, IMethodSymbol member) =>
        Assert.IsType<GenerationTarget>(GenerationTargets.Of(member, compilation, _install.Resolver(), CancellationToken.None));

    /// <summary>A model as the generator writes it for a member: its original definition's id, its assembly at its version.</summary>
    /// <param name="member">The member.</param>
    private static LibraryModel Model(IMethodSymbol member) => new(member.GetDocumentationCommentId()!, [VersionRange(member)], []);

    private static SupportedAssemblyVersion VersionRange(IMethodSymbol member)
    {
        var version = member.ContainingAssembly.Identity.Version;
        return new SupportedAssemblyVersion(member.ContainingAssembly.Name, version, new Version(version.Major, version.Minor, version.Build, version.Revision + 1));
    }

    /// <summary>Writes answers to the file of a key in the test's repository and reads the file back, as a later run would.</summary>
    /// <param name="key">The file's key.</param>
    /// <param name="answers">The answers.</param>
    private GeneratedModelFile Stored(GeneratedFileKey key, params GeneratedModelAnswer[] answers)
    {
        Assert.Empty(GeneratedModelFiles.Write(GeneratedModelFiles.Read(Repository, key), answers, CancellationToken.None));
        var file = GeneratedModelFiles.Read(Repository, key);
        Assert.False(file.IsUnreadable);
        Assert.Empty(file.Diagnostics);
        return file;
    }
}
