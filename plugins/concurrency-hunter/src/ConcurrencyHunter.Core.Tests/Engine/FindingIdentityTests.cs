using System.Text.Json;
using System.Text.RegularExpressions;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class FindingIdentityTests
{
    private const string STATE = "public static class State { public static int Value; public static int Other; }\n";
    private const string GATE = "public static class Gate { public static readonly object Lock = new(); }\n";
    private static readonly Regex FINGERPRINT = new("^[0-9a-f]{16}$");

    [Fact]
    public void Three_roots_on_one_site_fold_into_one_finding_with_six_occurrences_in_one_group()
    {
        var (_, findings, groups) = Fold("""
            public sealed class Log { private string? _last; public void Record(string value) => _last = value; }
            public class OneController(Log log) : ControllerBase { public void Post() => log.Record("one"); }
            public class TwoController(Log log) : ControllerBase { public void Post() => log.Record("two"); }
            public class ThreeController(Log log) : ControllerBase { public void Post() => log.Record("three"); }
            """ + Startup("services.AddSingleton<Log>();"));

        var finding = Assert.Single(findings);
        Assert.Equal(6, finding.OccurrenceCount);
        Assert.Equal(3, finding.Occurrences.Count);
        Assert.Equal("Log.Record(string)", finding.AccessA.Symbol);
        var group = Assert.Single(groups);
        Assert.Equal(6, group.OccurrenceCount);
        Assert.Equal([finding.FindingId], group.FindingIds);
    }

    [Fact]
    public void Two_distinct_sites_stay_two_findings()
    {
        var (_, findings, groups) = Fold("""
            public sealed class Log { private string? _last; public void Record(string value) => _last = value; public void Reset() => _last = null; }
            public class OneController(Log log) : ControllerBase { public void Post() => log.Record("one"); }
            public class TwoController(Log log) : ControllerBase { public void Post() => log.Reset(); }
            """ + Startup("services.AddSingleton<Log>();"));

        Assert.Equal(3, findings.Count);
        Assert.Equal(3, findings.Select(finding => finding.Fingerprint).Distinct().Count());
        Assert.Single(findings, finding => finding.AccessA.BodyId != finding.AccessB.BodyId);
        Assert.Equal(3, Assert.Single(groups).OccurrenceCount);
    }

    [Fact]
    public void Two_same_named_members_of_two_assemblies_are_two_sites()
    {
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
                ProjectReferences = [("App", "First"), ("App", "Second"), ("App", "Shared"), ("First", "Shared"), ("Second", "Shared")]
            },
            ("Shared", "State.cs", "public static class State { public static int Value; }"),
            ("First", "Helper.cs", "internal static class Helper { public static void Touch() => State.Value = 1; } public static class FirstEntry { public static void Run() => Helper.Touch(); }"),
            ("Second", "Helper.cs", "internal static class Helper { public static void Touch() => State.Value = 2; } public static class SecondEntry { public static void Run() => Helper.Touch(); }"),
            ("App", "Program.cs", "System.Console.WriteLine();"),
            ("App", "Controllers.cs", Usings + """
                public class RunController : ControllerBase { public void Post() { FirstEntry.Run(); SecondEntry.Run(); } }
                """ + Startup()));
        var run = AnalyzeScope(solution, "scope:App");
        var (findings, _) = ConflictFindings.Create(run.Pairs.Pairs, run.Collection.Accesses, CancellationToken.None);

        var selfPairs = findings.Where(finding => finding.AccessA.BodyId == finding.AccessB.BodyId).ToArray();
        Assert.Equal(2, selfPairs.Length);
        Assert.All(selfPairs, finding => Assert.Equal(("Helper.Touch()", "Helper.Touch()"), (finding.AccessA.Symbol, finding.AccessB.Symbol)));
        Assert.NotEqual(selfPairs[0].AccessA.BodyId, selfPairs[1].AccessA.BodyId);
        Assert.NotEqual(selfPairs[0].Fingerprint, selfPairs[1].Fingerprint);
        Assert.Equal(3, findings.Count);
    }

    [Fact]
    public void Two_same_kind_sites_in_one_member_have_different_fingerprints()
    {
        var (_, findings, _) = Fold(STATE + """
            public class GateController : ControllerBase { public void Post() { State.Value = 1; State.Value = 2; } }
            """ + Startup());

        Assert.Equal(3, findings.Count);
        Assert.All(findings, finding => Assert.Equal(finding.AccessA.BodyId, finding.AccessB.BodyId));
        Assert.Equal(3, findings.Select(finding => finding.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void Two_site_pairs_whose_accesses_share_a_start_position_have_different_fingerprints()
    {
        var wildcard = FindingTestData.Resource("static:Chain", "*") with { IsWildcard = true };
        Access Side(string field, AccessOperation operation, int endColumn, int operationId) =>
            FindingTestData.Access(FindingTestData.Resource("static:Chain", field), operation, "root-a", "Chain.Write()",
                                   new SourceSpan("Case.cs", 7, 40, 7, endColumn)) with { BodyId = "body:Chain.Write", OperationId = operationId };
        var write = Side("Value", AccessOperation.Write, 114, 9) with { Resource = wildcard };
        var pairs = new[]
        {
            new AccessPair(Side("First", AccessOperation.Read, 45, 1), write, PairProtection.UNPROTECTED) { Resource = wildcard },
            new AccessPair(Side("Next", AccessOperation.Read, 85, 5), write, PairProtection.UNPROTECTED) { Resource = wildcard }
        };

        var (findings, _) = ConflictFindings.Create(pairs, CancellationToken.None);

        Assert.Equal(2, findings.Count);
        Assert.NotEqual(findings[0].Fingerprint, findings[1].Fingerprint);
    }

    [Fact]
    public void Lazy_construction_reached_by_two_roots_has_an_occurrence_for_each_root()
    {
        var (_, findings, _) = Fold(STATE + LAZY_CLOCK + """
            public class GateController(IServiceProvider services) : ControllerBase { public void Post() => GC.KeepAlive(services.GetRequiredService<Clock>()); }
            public class OtherController(IServiceProvider services) : ControllerBase { public void Post() => GC.KeepAlive(services.GetRequiredService<Clock>()); }
            """ + Startup("services.AddSingleton<Clock>(); services.AddHostedService<Worker>();"));

        var finding = Assert.Single(findings, item => item.AccessA.BodyId != item.AccessB.BodyId);
        var constructor = new[] { finding.AccessA, finding.AccessB }.Single(access => access.BodyId.Contains("Clock.#ctor", StringComparison.Ordinal));
        Assert.StartsWith("construction:", constructor.ExecutionId, StringComparison.Ordinal);
        var roots = finding.Occurrences.Select(occurrence => string.Join(" ", new[] { occurrence.RootA.RootId, occurrence.RootB.RootId }
                                                                                   .Select(id => id.Contains("GateController", StringComparison.Ordinal) ? "gate"
                                                                                                 : id.Contains("OtherController", StringComparison.Ordinal) ? "other"
                                                                                                 : id.Contains("Worker", StringComparison.Ordinal) ? "worker" : id)
                                                                                   .Order(StringComparer.Ordinal)))
                                       .Order(StringComparer.Ordinal)
                                       .ToArray();
        Assert.Equal(["gate worker", "other worker"], roots);
        Assert.Equal(2, finding.OccurrenceCount);
    }

    [Fact]
    public void Construction_triggered_finding_evidence_names_the_triggering_root()
    {
        var (_, findings, _) = Fold(STATE + LAZY_CLOCK + """
            public class GateController(IServiceProvider services) : ControllerBase { public void Post() => GC.KeepAlive(services.GetRequiredService<Clock>()); }
            """ + Startup("services.AddSingleton<Clock>(); services.AddHostedService<Worker>();"));

        var finding = Assert.Single(findings, item => item.AccessA.BodyId != item.AccessB.BodyId);
        var constructor = new[] { finding.AccessA, finding.AccessB }.Single(access => access.BodyId.Contains("Clock.#ctor", StringComparison.Ordinal));
        Assert.Equal("construction", constructor.Root.ProviderId);
        Assert.True(IsGate(constructor.PathRoot));
        var texts = finding.Evidence.Where(item => item.Kind is "overlap" or "access-a" or "access-b").Select(item => item.Text)
                           .Concat(finding.ConcurrencyEvidence)
                           .ToArray();
        Assert.All(texts, text => Assert.DoesNotContain(constructor.Root.Display, text, StringComparison.Ordinal));
        Assert.Contains("GateController", Assert.Single(finding.ConcurrencyEvidence), StringComparison.Ordinal);
        Assert.Contains(texts, text => text.Contains($"under root {constructor.PathRoot.Display}", StringComparison.Ordinal));
    }

    [Fact]
    public void Occurrences_are_deduplicated_and_ordered()
    {
        var (run, findings, _) = Fold(HELPER + GATE + """
            public class OneController : ControllerBase { public void Post() { Helper.Touch(); lock (Gate.Lock) { Helper.Touch(); } } }
            public class TwoController : ControllerBase { public void Post() { Helper.Touch(); Helper.Touch(); } }
            """ + Startup());

        Assert.True(run.PairsOn("Last").Count > 3);
        var finding = Assert.Single(findings);
        Assert.Equal(3, finding.OccurrenceCount);
        var keys = finding.Occurrences.Select(occurrence => (occurrence.RootA.RootId, occurrence.RootB.RootId)).ToArray();
        Assert.Equal(keys.Distinct().Count(), keys.Length);
        Assert.Equal(keys.OrderBy(key => key.Item1, StringComparer.Ordinal).ThenBy(key => key.Item2, StringComparer.Ordinal), keys);
        Assert.All(finding.Occurrences, occurrence => Assert.True(string.CompareOrdinal(occurrence.RootA.RootId, occurrence.RootB.RootId) <= 0));
    }

    [Fact]
    public void Two_paths_from_one_root_to_one_site_are_one_occurrence_with_the_discovery_path()
    {
        var (_, findings, _) = Fold(HELPER + """
            public class GateController : ControllerBase
            {
                public void Post() { Relay(); Helper.Touch(); }
                private void Relay() => Helper.Touch();
            }
            """ + Startup());

        var finding = Assert.Single(findings);
        var occurrence = Assert.Single(finding.Occurrences);
        Assert.Equal(["GateController.Post()", "Helper.Touch()"], occurrence.CallPathA);
        Assert.Equal(occurrence.CallPathA, occurrence.CallPathB);
    }

    [Fact]
    public void Diamond_paths_of_equal_length_pick_the_smallest_path()
    {
        var (_, findings, _) = Fold(HELPER + """
            public class GateController : ControllerBase
            {
                public void Post() { A(); B(); }
                private void A() => Helper.Touch();
                private void B() => Helper.Touch();
            }
            """ + Startup());

        var occurrence = Assert.Single(Assert.Single(findings).Occurrences);
        Assert.Equal(["GateController.Post()", "GateController.A()", "Helper.Touch()"], occurrence.CallPathA);
    }

    [Fact]
    public void Occurrence_path_into_a_factory_body_goes_through_the_triggering_resolution()
    {
        var (_, findings, _) = Fold(STATE + Worker("State.Value = 2;") + """
            public sealed class Tag { }
            public class GateController(IServiceProvider services) : ControllerBase { public void Post() => GC.KeepAlive(services.GetRequiredService<Tag>()); }
            """ + Startup("services.AddTransient<Tag>(_ => { State.Value = 1; return new Tag(); }); services.AddHostedService<Worker>();"));

        var finding = Assert.Single(findings, item => item.AccessA.BodyId != item.AccessB.BodyId);
        var occurrence = Assert.Single(finding.Occurrences, item => IsGate(item.RootA) || IsGate(item.RootB));
        var path = IsGate(occurrence.RootA) ? occurrence.CallPathA : occurrence.CallPathB;
        var factory = new[] { finding.AccessA, finding.AccessB }.Single(access => access.BodyId.Contains("#lambda", StringComparison.Ordinal));
        Assert.Equal(["GateController.Post()", factory.Symbol], path);
    }

    [Fact]
    public void Occurrence_path_into_an_injected_singleton_constructor_starts_at_the_root_entry()
    {
        var (_, findings, _) = Fold(STATE + Worker("State.Value = 2;") + """
            public sealed class Clock { public Clock() => State.Value = 1; }
            public class GateController : ControllerBase { public void Get([FromServices] Clock clock) => GC.KeepAlive(clock); }
            """ + Startup("services.AddSingleton<Clock>(); services.AddHostedService<Worker>();"));

        var finding = Assert.Single(findings, item => item.AccessA.BodyId != item.AccessB.BodyId);
        var constructor = new[] { finding.AccessA, finding.AccessB }.Single(access => access.BodyId.Contains("Clock.#ctor", StringComparison.Ordinal));
        Assert.StartsWith("construction:", constructor.ExecutionId, StringComparison.Ordinal);
        Assert.Equal(["GateController.Get(Clock)", constructor.Symbol], constructor.CallPath);
        Assert.Contains(finding.Occurrences, occurrence => IsGate(occurrence.RootA) || IsGate(occurrence.RootB));
    }

    [Fact]
    public void Occurrence_path_into_a_transitively_injected_constructor_goes_through_the_injecting_constructor()
    {
        var (_, findings, _) = Fold(STATE + Worker("State.Value = 2;") + """
            public sealed class Inner { public Inner() => State.Value = 1; }
            public sealed class Outer { public Outer(Inner inner) => GC.KeepAlive(inner); }
            public class GateController : ControllerBase { public void Get([FromServices] Outer outer) => GC.KeepAlive(outer); }
            """ + Startup("services.AddTransient<Outer>(); services.AddTransient<Inner>(); services.AddHostedService<Worker>();"));

        var finding = Assert.Single(findings, item => item.AccessA.BodyId != item.AccessB.BodyId);
        var occurrence = Assert.Single(finding.Occurrences, item => IsGate(item.RootA) || IsGate(item.RootB));
        var path = IsGate(occurrence.RootA) ? occurrence.CallPathA : occurrence.CallPathB;
        Assert.Equal(3, path.Count);
        Assert.Equal("GateController.Get(Outer)", path[0]);
        Assert.Contains("Outer", path[1], StringComparison.Ordinal);
        Assert.Contains("Inner", path[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Occurrence_path_into_a_type_initializer_goes_through_the_first_static_use()
    {
        var (_, findings, _) = Fold(STATE + Worker("var cancelled = token.IsCancellationRequested; GC.KeepAlive(cancelled); State.Value = 2;") + """
            public static class Config { public static int Other = 1; static Config() { State.Value = 1; } }
            public class GateController : ControllerBase { public int Get() { var early = Config.Other; var again = Config.Other; return early + again; } }
            """ + Startup("services.AddHostedService<Worker>();"));

        var finding = Assert.Single(findings, item => item.Resource.AccessPath.SequenceEqual(["Value"]) && item.AccessA.BodyId != item.AccessB.BodyId);
        var occurrence = Assert.Single(finding.Occurrences);
        Assert.Equal(2, new[] { occurrence.RootA, occurrence.RootB }.Count(root => IsGate(root) || root.RootId.Contains("Worker", StringComparison.Ordinal)));
        Assert.Contains(new[] { occurrence.RootA, occurrence.RootB }, IsGate);
        var path = IsGate(occurrence.RootA) ? occurrence.CallPathA : occurrence.CallPathB;
        Assert.Equal("GateController.Get()", path[0]);
        Assert.Equal(2, path.Count);
        Assert.Contains("Config", path[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_root_pairs_under_two_contexts_pick_the_least_protected_pair()
    {
        var (_, findings, _) = Fold(HELPER + GATE + Worker("Helper.Last = new object();") + """
            public class GateController : ControllerBase { public void Post() { lock (Gate.Lock) { Helper.Touch(); } Helper.Touch(); } }
            """ + Startup("services.AddHostedService<Worker>();"));

        var finding = Assert.Single(findings, item => item.AccessA.BodyId != item.AccessB.BodyId);
        var occurrence = Assert.Single(finding.Occurrences);
        Assert.Equal(PairProtection.UNPROTECTED, occurrence.Protection);
        Assert.Equal(PairProtection.UNPROTECTED, finding.ProtectionResult);
        Assert.Empty(finding.AccessA.HeldProtection);
        Assert.Empty(finding.AccessB.HeldProtection);
    }

    [Fact]
    public void Listed_occurrences_are_the_first_three()
    {
        var (_, findings, _) = Fold(HELPER + """
            public class OneController : ControllerBase { public void Post() => Helper.Touch(); }
            public class TwoController : ControllerBase { public void Post() => Helper.Touch(); }
            public class ThreeController : ControllerBase { public void Post() => Helper.Touch(); }
            public class FourController : ControllerBase { public void Post() => Helper.Touch(); }
            """ + Startup());

        var finding = Assert.Single(findings);
        Assert.Equal(10, finding.OccurrenceCount);
        Assert.Equal(3, finding.Occurrences.Count);
        var first = finding.Occurrences[0].RootA.RootId;
        Assert.Equal([first, first, first], finding.Occurrences.Select(occurrence => occurrence.RootA.RootId));
        Assert.Equal(finding.Occurrences.Select(occurrence => occurrence.RootB.RootId).Order(StringComparer.Ordinal),
                     finding.Occurrences.Select(occurrence => occurrence.RootB.RootId));
    }

    [Fact]
    public void Confidence_is_the_maximum_over_occurrences()
    {
        var resource = FindingTestData.Resource("static:State", "Value");
        var wildcard = resource with { AccessPath = ["*"], IsWildcard = true };
        var sideA = FindingTestData.Access(resource, AccessOperation.Write, "root-a", "A.Post()", new SourceSpan("Case.cs", 1, 1, 1, 9)) with { BodyId = "body:A" };
        var sideB = FindingTestData.Access(resource, AccessOperation.Write, "root-b", "B.Post()", new SourceSpan("Case.cs", 2, 1, 2, 9)) with { BodyId = "body:B" };
        var sideC = sideB with { Root = sideB.Root with { RootId = "root-c" } };
        var exact = new AccessPair(sideA, sideB, PairProtection.UNPROTECTED);
        var other = new AccessPair(sideA, sideC, PairProtection.UNPROTECTED);
        var wide = new AccessPair(sideA with { Resource = wildcard }, sideB with { Resource = wildcard }, PairProtection.UNPROTECTED) { Resource = wildcard };

        var (findings, _) = ConflictFindings.Create([exact, other, wide], CancellationToken.None);

        var high = Assert.Single(findings, finding => !finding.Resource.IsWildcard);
        Assert.Equal(2, high.OccurrenceCount);
        Assert.Equal(("High", 90), (high.Confidence.Label, high.Confidence.Score));
        var medium = Assert.Single(findings, finding => finding.Resource.IsWildcard);
        Assert.True(medium.Confidence.Score < high.Confidence.Score);
    }

    [Fact]
    public void Mixed_protection_occurrences_take_the_least_protected_result()
    {
        const string unlocked = "public class OpenController : ControllerBase { public void Post() => Helper.Touch(); }\n";
        const string locked = "public class OpenController : ControllerBase { public void Post() { lock (Gate.Lock) { Helper.Touch(); } } }\n";
        var source = HELPER + GATE + Worker("Helper.Last = new object();") + """
            public class LockedController : ControllerBase { public void Post() { lock (Gate.Lock) { Helper.Touch(); } } }
            """;

        var (_, mixed, _) = Fold(source + unlocked + Startup("services.AddHostedService<Worker>();"));
        var (_, allLocked, _) = Fold(source + locked + Startup("services.AddHostedService<Worker>();"));

        var finding = Assert.Single(mixed, item => item.AccessA.BodyId != item.AccessB.BodyId);
        Assert.Equal(2, finding.OccurrenceCount);
        Assert.Equal(PairProtection.UNPROTECTED, finding.ProtectionResult);
        Assert.Contains(finding.Occurrences, occurrence => occurrence.Protection == PairProtection.PARTIAL);
        var protection = Assert.Single(finding.Evidence, item => item.Kind == "protection").Text;
        var overlap = Assert.Single(finding.Evidence, item => item.Kind == "overlap").Text;
        Assert.StartsWith("Protection: unprotected;", protection, StringComparison.Ordinal);
        Assert.Contains("OpenController", overlap, StringComparison.Ordinal);
        Assert.DoesNotContain("LockedController", overlap, StringComparison.Ordinal);
        Assert.DoesNotContain("LockedController", string.Join(" ", finding.ConcurrencyEvidence), StringComparison.Ordinal);
        var lockedFinding = Assert.Single(allLocked, item => item.AccessA.BodyId != item.AccessB.BodyId);
        Assert.Equal(PairProtection.PARTIAL, lockedFinding.ProtectionResult);
        Assert.NotEqual(lockedFinding.Fingerprint, finding.Fingerprint);
    }

    [Fact]
    public void Fingerprint_survives_a_moved_line()
    {
        var before = Fingerprints(STATE + Pair("State.Value = 1;", "State.Value = 2;") + Startup("services.AddHostedService<Worker>();"));
        var after = Fingerprints("\n\n\n" + STATE + "\n\n" + Pair("GC.KeepAlive(this);\n        State.Value = 1;", "State.Value = 2;") +
                                 Startup("services.AddHostedService<Worker>();"));

        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Fingerprint_survives_a_renamed_local()
    {
        var before = Fingerprints(STATE + Pair("var value = 1; State.Value = value;", "State.Value = 2;") + Startup("services.AddHostedService<Worker>();"));
        var after = Fingerprints(STATE + Pair("var renamed = 1; State.Value = renamed;", "State.Value = 2;") + Startup("services.AddHostedService<Worker>();"));

        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Fingerprint_survives_a_new_root()
    {
        var source = HELPER + Worker("Helper.Last = new object();") + "public class OneController : ControllerBase { public void Post() => Helper.Touch(); }\n";
        var (_, before, _) = Fold(source + Startup("services.AddHostedService<Worker>();"));
        var (_, after, _) = Fold(source + "public class TwoController : ControllerBase { public void Post() => Helper.Touch(); }\n" +
                                 Startup("services.AddHostedService<Worker>();"));

        var original = Assert.Single(before, item => item.AccessA.BodyId != item.AccessB.BodyId);
        var grown = Assert.Single(after, item => item.AccessA.BodyId != item.AccessB.BodyId);
        Assert.Equal(original.Fingerprint, grown.Fingerprint);
        Assert.True(grown.OccurrenceCount > original.OccurrenceCount);
        Assert.Equal(before.Select(item => item.Fingerprint).Order(), after.Select(item => item.Fingerprint).Order());
    }

    [Fact]
    public void Fingerprint_changes_with_rule_site_path_and_protection()
    {
        string Cross(string action, string worker = "State.Value = 2;", string extra = "") =>
            Single(STATE + GATE + extra + Pair(action, worker) + Startup("services.AddHostedService<Worker>();"));

        var baseline = Cross("State.Value = 1;");
        var rule = Cross("State.Value++;");
        var site = Cross("Helpers.Set();", extra: "public static class Helpers { public static void Set() => State.Value = 1; }\n");
        var path = Cross("State.Other = 1;", "State.Other = 2;");
        var protection = Cross("lock (Gate.Lock) { State.Value = 1; }");

        Assert.Equal(5, new[] { baseline, rule, site, path, protection }.Distinct().Count());
    }

    [Fact]
    public async Task Fingerprint_and_group_fingerprint_differ_between_two_process_scopes()
    {
        var program = Usings + """
            public class SyncController : ControllerBase { public void Post() => SharedState.Value = 1; }
            public sealed class SyncWorker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token) { SharedState.Value = 2; return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<SyncWorker>();");
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["Web"] = OutputKind.ConsoleApplication, ["Jobs"] = OutputKind.ConsoleApplication },
                ProjectAssemblyNames = new Dictionary<string, string> { ["Web"] = "App", ["Jobs"] = "App" },
                ProjectReferences = [("Web", "Shared"), ("Jobs", "Shared")]
            },
            ("Shared", "SharedState.cs", "public static class SharedState { public static int Value; }"),
            ("Web", "Program.cs", "System.Console.WriteLine();"),
            ("Web", "App.cs", program),
            ("Jobs", "Program.cs", "System.Console.WriteLine();"),
            ("Jobs", "App.cs", program));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        var crossing = result.Findings.Where(finding => finding.AccessA.BodyId != finding.AccessB.BodyId).ToArray();
        Assert.Equal(2, crossing.Length);
        Assert.NotEqual(crossing[0].Resource.Scope, crossing[1].Resource.Scope);
        Assert.Equal(crossing[0].AccessA.BodyId, crossing[1].AccessA.BodyId);
        Assert.NotEqual(crossing[0].Fingerprint, crossing[1].Fingerprint);
        Assert.NotEqual(crossing[0].GroupFingerprint, crossing[1].GroupFingerprint);
        Assert.Equal(2, result.Groups.Select(group => group.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void Fingerprint_distinguishes_numbered_registrations()
    {
        const string source = """
            public sealed class Log { public int Count; }
            public class GateController(Log log) : ControllerBase { public void Post() => log.Count = 1; }
            public sealed class Worker(Log log) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token) { log.Count = 2; return Task.CompletedTask; }
            }
            """;
        var (_, once, _) = Fold(source + Startup("services.AddSingleton<Log>(); services.AddHostedService<Worker>();"));
        var (_, twice, _) = Fold(source + Startup("services.AddSingleton<Log>(); services.AddSingleton<Log>(); services.AddHostedService<Worker>();"));

        var first = Assert.Single(once, item => item.AccessA.BodyId != item.AccessB.BodyId);
        var second = Assert.Single(twice, item => item.AccessA.BodyId != item.AccessB.BodyId);
        Assert.Equal("di:Log@Singleton", first.Resource.Region);
        Assert.Equal("di:Log@Singleton#2", second.Resource.Region);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.GroupFingerprint, second.GroupFingerprint);
    }

    [Fact]
    public void Fingerprint_distinguishes_same_named_types_of_two_assemblies()
    {
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
                ProjectReferences = [("App", "First"), ("App", "Second")]
            },
            ("First", "State.cs", "internal static class State { public static int Value; } public static class FirstWriter { public static void Write() => State.Value = 1; }"),
            ("Second", "State.cs", "internal static class State { public static int Value; } public static class SecondWriter { public static void Write() => State.Value = 1; }"),
            ("App", "Program.cs", "System.Console.WriteLine();"),
            ("App", "Controllers.cs", Usings + """
                public class RunController : ControllerBase { public void Post() { FirstWriter.Write(); SecondWriter.Write(); } }
                """ + Startup()));
        var run = AnalyzeScope(solution, "scope:App");
        var (findings, groups) = ConflictFindings.Create(run.Pairs.Pairs, run.Collection.Accesses, CancellationToken.None);

        Assert.Equal(2, findings.Count);
        Assert.Equal(findings[0].Resource.Region, findings[1].Resource.Region);
        Assert.NotEqual(findings[0].Resource.RegionKey, findings[1].Resource.RegionKey);
        Assert.NotEqual(findings[0].Fingerprint, findings[1].Fingerprint);
        Assert.Equal(2, groups.Select(group => group.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void Fingerprint_covers_receiver_and_delegate_regions()
    {
        var (_, findings, _) = Fold("""
            public sealed class Box { public int Value; }
            public sealed class Holder { public GateController? Last; }
            public static class Hooks { public static Action? Hook; }
            public class GateController(Holder holder) : ControllerBase
            {
                public int Count;
                public void Post() { holder.Last = this; Count = 1; Hooks.Hook = () => { }; ((Box)(object)Hooks.Hook).Value = 1; }
            }
            public sealed class Worker(Holder holder) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    holder.Last!.Count = 2;
                    ((Box)(object)Hooks.Hook!).Value = 2;
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Holder>(); services.AddHostedService<Worker>();"));

        var receiver = Assert.Single(findings, item => item.Resource.Region.StartsWith("receiver:", StringComparison.Ordinal) && item.AccessA.BodyId != item.AccessB.BodyId);
        var delegated = Assert.Single(findings, item => item.Resource.Region.StartsWith("delegate:", StringComparison.Ordinal) && item.AccessA.BodyId != item.AccessB.BodyId);
        Assert.Equal("receiver|Fixture:GateController", receiver.Resource.RegionKey);
        Assert.StartsWith("delegate|body:Fixture:M:GateController.Post#", delegated.Resource.RegionKey, StringComparison.Ordinal);
        foreach (var finding in new[] { receiver, delegated })
        {
            Assert.Matches(FINGERPRINT, finding.Fingerprint);
            Assert.Matches(FINGERPRINT, finding.GroupFingerprint);
            Assert.DoesNotContain("root:", finding.Resource.RegionKey, StringComparison.Ordinal);
            Assert.DoesNotContain("invocation", finding.Resource.RegionKey, StringComparison.Ordinal);
            Assert.NotEqual(finding.Resource.RegionId, finding.Resource.RegionKey);
        }
    }

    [Fact]
    public void Group_fingerprint_survives_a_new_site_pair()
    {
        var (_, before, beforeGroups) = Fold(STATE + Pair("State.Value = 1;", "State.Value = 2;") + Startup("services.AddHostedService<Worker>();"));
        var (_, after, afterGroups) = Fold(STATE + Pair("State.Value = 1;", "State.Value = 2;") +
                                           "public class OtherController : ControllerBase { public void Post() => State.Value = 3; }\n" +
                                           Startup("services.AddHostedService<Worker>();"));

        Assert.True(after.Count > before.Count);
        Assert.Equal(Assert.Single(beforeGroups).Fingerprint, Assert.Single(afterGroups).Fingerprint);
        Assert.All(after, finding => Assert.Equal(beforeGroups[0].Fingerprint, finding.GroupFingerprint));
    }

    [Fact]
    public async Task Findings_json_carries_schema_2_1_fingerprints_and_occurrences()
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + HELPER + """
            public class OneController : ControllerBase { public void Post() => Helper.Touch(); }
            public class TwoController : ControllerBase { public void Post() => Helper.Touch(); }
            """ + Startup()));
        var analysis = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        using var json = JsonDocument.Parse(ReportRenderer.Render(ReportingTestData.CreateReport(analysis)).FindingsJson);
        var root = json.RootElement;

        Assert.Equal("2.2", root.GetProperty("schemaVersion").GetString());
        var finding = Assert.Single(root.GetProperty("findings").EnumerateArray());
        var group = Assert.Single(root.GetProperty("groups").EnumerateArray());
        Assert.False(finding.TryGetProperty("stableId", out _));
        Assert.False(group.TryGetProperty("stableId", out _));
        Assert.Matches(FINGERPRINT, finding.GetProperty("fingerprint").GetString()!);
        Assert.Equal(group.GetProperty("fingerprint").GetString(), finding.GetProperty("groupFingerprint").GetString());
        Assert.Equal(3, finding.GetProperty("occurrenceCount").GetInt32());
        Assert.Equal(3, group.GetProperty("occurrenceCount").GetInt32());
        var occurrences = finding.GetProperty("occurrences").EnumerateArray().ToArray();
        Assert.Equal(3, occurrences.Length);
        Assert.All(occurrences, occurrence =>
        {
            Assert.Equal(2, occurrence.GetProperty("roots").GetArrayLength());
            var paths = occurrence.GetProperty("callPaths").EnumerateArray().ToArray();
            Assert.Equal(2, paths.Length);
            Assert.All(paths, path => Assert.Equal("Helper.Touch()", path.EnumerateArray().Last().GetString()));
        });
        Assert.NotEmpty(group.GetProperty("representativeLocations").EnumerateArray());
    }

    private const string HELPER = "public static class Helper { public static object? Last; public static void Touch() => Last = new object(); }\n";

    private const string LAZY_CLOCK = """
        public sealed class Clock { public Clock() => State.Value = 1; }
        public sealed class Worker : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken token) { State.Value = 2; return Task.CompletedTask; }
        }

        """;

    private static bool IsGate(AccessRoot root) => root.RootId.Contains("GateController", StringComparison.Ordinal);

    private static string Worker(string body) => $$"""
        public sealed class Worker : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken token)
            {
                {{body}}
                return Task.CompletedTask;
            }
        }

        """;

    /// <summary>A controller action and a worker, each running its statement.</summary>
    private static string Pair(string action, string worker) => $$"""
        public class GateController : ControllerBase
        {
            public void Post()
            {
                {{action}}
            }
        }

        """ + Worker(worker);

    private static (EngineRun Run, IReadOnlyList<Finding> Findings, IReadOnlyList<FindingGroup> Groups) Fold(string source)
    {
        var run = Analyze(source);
        var (findings, groups) = ConflictFindings.Create(run.Pairs.Pairs, run.Collection.Accesses, CancellationToken.None);
        return (run, findings, groups);
    }

    private static IReadOnlyList<string> Fingerprints(string source) =>
        Fold(source).Findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The fingerprint of the one finding between the action and the worker.</summary>
    private static string Single(string source) =>
        Assert.Single(Fold(source).Findings, finding => finding.AccessA.PathRoot.RootId != finding.AccessB.PathRoot.RootId).Fingerprint;
}
