using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class ConflictTests
{
    private const string RootDirectory = @"C:\fixture";

    [Fact]
    public async Task Write_in_an_action_pairs_with_itself()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public void Set() { _value = 1; }
            }
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(finding.AccessA, finding.AccessB);
        Assert.Equal(AccessOperation.Write, finding.AccessA.Operation);
    }

    [Fact]
    public async Task Read_in_an_action_does_not_pair_with_itself()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public int Get() { return _value; }
            }
            """);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Write_and_read_in_two_actions_form_one_finding()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public void Set() { _value = 1; }
                public int Get() { return _value; }
            }
            """);

        var finding = Assert.Single(result.Findings, finding =>
            finding.AccessA.Root.RootId != finding.AccessB.Root.RootId);
        Assert.Contains(AccessOperation.Write, new[] { finding.AccessA.Operation, finding.AccessB.Operation });
        Assert.Contains(AccessOperation.Read, new[] { finding.AccessA.Operation, finding.AccessB.Operation });
    }

    [Fact]
    public async Task Read_and_write_in_one_action_are_explained_as_the_action_overlapping_itself()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public int Replace()
                {
                    var old = _value;
                    _value = 1;
                    return old;
                }
            }
            """);

        var finding = Assert.Single(result.Findings, finding =>
            finding.AccessA.Operation != finding.AccessB.Operation);
        Assert.Equal(finding.AccessA.Root.RootId, finding.AccessB.Root.RootId);
        Assert.Equal(
            ["The same ControllerBase action may run concurrently with itself."],
            finding.ConcurrencyEvidence);
    }

    [Fact]
    public async Task Read_read_pair_is_not_a_finding()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public int First() { return _value; }
                public int Second() { return _value; }
            }
            """);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Common_static_lock_on_both_accesses_suppresses_the_pair()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static readonly object Gate = new();
                private static int _value;
                public void Set() { lock (Gate) { _value = 1; } }
                public int Get() { lock (Gate) { return _value; } }
            }
            """);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Lock_on_only_one_access_is_a_partial_protection_finding()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static readonly object Gate = new();
                private static int _value;
                public void Set() { lock (Gate) { _value = 1; } }
                public int Get() { return _value; }
            }
            """);

        Assert.Equal("partial", Assert.Single(result.Findings).ProtectionResult);
    }

    [Fact]
    public async Task Different_static_locks_are_a_different_identity_finding()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static readonly object FirstGate = new();
                private static readonly object SecondGate = new();
                private static int _value;
                public void Set() { lock (FirstGate) { _value = 1; } }
                public int Get() { lock (SecondGate) { return _value; } }
            }
            """);

        Assert.Equal("different-identity", Assert.Single(result.Findings).ProtectionResult);
    }

    [Fact]
    public async Task Every_finding_is_DCA1001_High_85_with_path_feasibility_uncertainty()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public void Set() { _value = 1; }
            }
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal("High", finding.Confidence.Label);
        Assert.Equal(85, finding.Confidence.Score);
        Assert.Equal(new ConfidenceComponents(25, 20, 20, 20, 0), finding.Confidence.Components);
        Assert.Equal(["Path feasibility is not analyzed in this version."], finding.Uncertainty);
        Assert.Equal("High", Assert.Single(result.Groups).ConfidenceLabel);
    }

    [Fact]
    public async Task Duplicate_pairs_with_the_same_identity_yield_one_finding()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public void Set()
                {
                    _value = 1;
                    _value = 2;
                }
            }
            """);

        Assert.Equal(2, result.Accesses.Count(access => Field(access.Resource) == "_value"));
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task Overloads_with_the_same_display_symbol_keep_distinct_findings()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            namespace A { public sealed class Payload { } }
            namespace B { public sealed class Payload { } }
            public sealed class PayloadController : ControllerBase
            {
                private static int _value;
                public void Post(A.Payload payload) { _value = 1; }
                public void Post(B.Payload payload) { _value = 2; }
            }
            """);

        Assert.Single(result.Roots.Select(root => root.Symbol).Distinct(StringComparer.Ordinal));
        Assert.Equal(2, result.Roots.Select(root => root.RootId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, result.Findings.Count);
        Assert.Equal(3, result.Findings.Select(finding => finding.StableId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Same_named_fields_and_locks_in_two_projects_neither_pair_nor_protect_each_other()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;
            namespace Demo;
            public static class State
            {
                public static readonly object Gate = new();
                public static int Value;
            }
            public sealed class ValuesController : ControllerBase
            {
                public void Set() { lock (State.Gate) { State.Value = 1; } }
            }
            """;
        var solution = FixtureSolution.CreateProjects(
            ("First", "ValuesController.cs", source),
            ("Second", "ValuesController.cs", source));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, RootDirectory, CancellationToken.None);

        var writes = result.Accesses.Where(access => Field(access.Resource) == "Value").ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal(["First", "Second"], writes.Select(access => access.Resource.Assembly).Order());
        Assert.Equal(2, writes.SelectMany(access => access.HeldProtectionIds).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Findings_on_one_resource_share_one_group_and_other_resources_get_their_own()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _first;
                private static int _second;
                public void SetFirst() { _first = 1; }
                public void ReplaceFirst() { _first = 2; }
                public void SetSecond() { _second = 1; }
            }
            """);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(["G1", "G2"], result.Groups.Select(group => group.GroupId));
        Assert.Equal(["_first", "_second"], result.Groups.Select(group => Field(group.Resource)));
        Assert.Equal(3, result.Groups[0].FindingIds.Count);
        Assert.Single(result.Groups[1].FindingIds);
        Assert.All(result.Findings.Where(finding => Field(finding.Resource) == "_first"),
            finding => Assert.Equal("G1", finding.GroupId));
    }

    [Fact]
    public async Task Evidence_ids_follow_finding_order_with_six_items_each()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            namespace Demo;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public void Set() { _value = 1; }
            }
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("F1", finding.FindingId);
        Assert.Equal(
            ["F1.A", "F1.B", "F1.R", "F1.O", "F1.P", "F1.S"],
            finding.Evidence.Select(evidence => evidence.Id));
        Assert.Equal(
            ["access-a", "access-b", "resource", "overlap", "protection", "scenario"],
            finding.Evidence.Select(evidence => evidence.Kind));
        Assert.Equal(
            "Access A: Demo.ValuesController.Set() performs write on _value at Controller.cs:6 under root " +
            "ControllerBase action Demo.ValuesController.Set(); holds no protection.",
            finding.Evidence[0].Text);
        Assert.Equal(
            "Resource: static field Demo.ValuesController._value in region static:Demo.ValuesController.",
            finding.Evidence[2].Text);
        Assert.Equal("Protection: unprotected.", finding.Evidence[4].Text);
    }

    [Fact]
    public async Task Scenarios_for_write_write_write_read_and_read_modify_write()
    {
        var result = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ScenarioController : ControllerBase
            {
                private static int _ww;
                private static int _wr;
                private static int _rmw;
                public void WriteWrite() { _ww = 1; }
                public int ReadWrite() { return _wr; }
                public void WriteRead() { _wr = 1; }
                public void Modify() { _rmw++; }
                public void Overwrite() { _rmw = 1; }
            }
            """);

        var writeWrite = Assert.Single(result.Findings, finding => Field(finding.Resource) == "_ww");
        Assert.Equal(
            ["A writes `_ww`", "B writes `_ww` before A's value is used", "One of the two writes is lost"],
            writeWrite.Scenario);

        var writeRead = Assert.Single(result.Findings, finding =>
            Field(finding.Resource) == "_wr" && finding.AccessA.Operation != finding.AccessB.Operation);
        Assert.Equal(
            ["B writes `_wr`", "A reads `_wr` at the same time", "A observes either the old or the new value"],
            writeRead.Scenario);

        var readModifyWrite = Assert.Single(result.Findings, finding =>
            Field(finding.Resource) == "_rmw" &&
            new[] { finding.AccessA.Operation, finding.AccessB.Operation }
                .Contains(AccessOperation.ReadModifyWrite) &&
            new[] { finding.AccessA.Operation, finding.AccessB.Operation }.Contains(AccessOperation.Write));
        Assert.Equal(
            ["A reads `_rmw`", "B writes `_rmw`",
                "A writes a value computed from its stale read, overwriting B's update"],
            readModifyWrite.Scenario);
    }

    [Fact]
    public async Task Analyzing_the_same_solution_twice_gives_identical_ids_and_order()
    {
        var solution = FixtureSolution.Create(("Controller.cs", """
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _first;
                private static int _second;
                public void SetFirst() { _first = 1; }
                public void ReplaceFirst() { _first = 2; }
                public void SetSecond() { _second = 1; }
            }
            """));

        var first = await PhaseOneAnalyzer.AnalyzeAsync(solution, RootDirectory, CancellationToken.None);
        var second = await PhaseOneAnalyzer.AnalyzeAsync(solution, RootDirectory, CancellationToken.None);

        Assert.Equal(
            first.Findings.Select(finding =>
                $"{finding.FindingId}|{finding.StableId}|{finding.GroupId}|{finding.AccessA.Root.RootId}|{finding.AccessB.Root.RootId}"),
            second.Findings.Select(finding =>
                $"{finding.FindingId}|{finding.StableId}|{finding.GroupId}|{finding.AccessA.Root.RootId}|{finding.AccessB.Root.RootId}"));
        Assert.Equal(
            first.Groups.Select(group => $"{group.GroupId}|{group.StableId}|{string.Join(",", group.FindingIds)}"),
            second.Groups.Select(group => $"{group.GroupId}|{group.StableId}|{string.Join(",", group.FindingIds)}"));
    }

    private static Task<AnalysisResult> Analyze(string source) =>
        PhaseOneAnalyzer.AnalyzeAsync(
            FixtureSolution.Create(("Controller.cs", source)),
            RootDirectory,
            CancellationToken.None);

    private static string Field(ResourceId resource) => resource.AccessPath[resource.AccessPath.Count - 1];
}
