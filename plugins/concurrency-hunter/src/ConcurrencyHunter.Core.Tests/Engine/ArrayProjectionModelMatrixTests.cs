using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ArrayProjectionModelMatrixTests
{
    public static IEnumerable<object[]> Clauses()
    {
        foreach (var member in new[] { "CopyTo", "Clear" })
        foreach (var receiver in new[] { "_state.Vector", "_state.Matrix", "stoppingToken.IsCancellationRequested ? _state.Vector : _state.Matrix" })
        foreach (var clause in new[] { "empty", "reads-deep", "writes-arg", "writes-cells", "stores", "keeps", "kept", "kept-argument", "nested-kept", "this", "opaque" })
        foreach (var through in member == "CopyTo" ? new[] { "ICollection", "ICollection<object>" } : new[] { "IList" })
            if (through != "ICollection<object>" || receiver == "_state.Vector")
                yield return [member, receiver, clause, through];
    }

    [Theory]
    [MemberData(nameof(Clauses))]
    public async Task Projected_model_agrees_with_the_direct_call_oracle(string member, string receiver, string clause, string through)
    {
        var target = member == "Clear" ? "array" : "this";
        var form = clause switch
        {
            "empty" => "\"effects\":{}",
            "opaque" => "\"opaque\":true",
            "reads-deep" or "writes-arg" or "writes-cells" => $"\"effects\":{{\"array\":[\"{clause}\"]}}",
            "stores" => "\"effects\":{\"array\":[\"writes-cells\"]},\"stores\":{\"array\":[\"elements(arg:array)\"]}",
            "keeps" => $"\"effects\":{{}},\"keeps\":{{\"{target}\":[\"elements(arg:array)\"]}}",
            "kept" when member == "CopyTo" => "\"effects\":{\"array\":[\"writes-cells\"]},\"stores\":{\"array\":[\"elements(kept:this)\"]}",
            "kept" => "\"effects\":{},\"keeps\":{\"array\":[\"elements(kept:array)\"]}",
            "kept-argument" => "\"effects\":{},\"keeps\":{\"array\":[\"elements(kept:array)\"]}",
            "nested-kept" => $"\"effects\":{{}},\"keeps\":{{\"{target}\":[\"sequence(elements(kept:{target}))\"]}}",
            "this" when member == "CopyTo" => "\"effects\":{},\"keeps\":{\"array\":[\"this\"]}",
            "this" => "\"effects\":{},\"keeps\":{\"array\":[\"arg:array\"]}",
            _ => throw new InvalidOperationException(clause)
        };
        var models = Models(member, form);
        var direct = member == "Clear" ? "Array.Clear(source);" : "source.CopyTo(_state.Target, 0);";
        var projected = member == "Clear" ? $"(({through})source).Clear();" : $"(({through})source).CopyTo(_state.Target, 0);";
        var expected = Solution(receiver, direct);
        var actual = Solution(receiver, projected);
        var expectedRun = AnalyzeScope(expected, "Fixture", ModelCellFixture.Resolve(expected, models));
        var actualRun = AnalyzeScope(actual, "Fixture", ModelCellFixture.Resolve(actual, models));
        Assert.Equal(Accesses(expectedRun), Accesses(actualRun));
        foreach (var counter in new[] { CoverageCounters.KNOWN_CALL_PROJECT, CoverageCounters.KNOWN_CALL_BUILT_IN,
                                       CoverageCounters.OPAQUE_CALL, CoverageCounters.OPAQUE_BY_PROJECT, CoverageCounters.MODEL_ENTRY_REJECTED })
            Assert.Equal(expectedRun.Counter(counter), actualRun.Counter(counter));
        Assert.Equal(Gaps(expectedRun), Gaps(actualRun));
        using var repo = new CellModelRepository(models);
        var expectedResult = await PhaseOneAnalyzer.AnalyzeAsync(expected, ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        var actualResult = await PhaseOneAnalyzer.AnalyzeAsync(actual, ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        Assert.Equal(expectedResult.Findings.Select(finding => finding.RuleId + "|" + finding.Resource.Region + "|" + string.Join('.', finding.Resource.AccessPath)).Order(StringComparer.Ordinal),
                     actualResult.Findings.Select(finding => finding.RuleId + "|" + finding.Resource.Region + "|" + string.Join('.', finding.Resource.AccessPath)).Order(StringComparer.Ordinal));
        if (clause == "kept")
            Assert.Contains(expectedResult.Findings, finding => finding.Resource.Member.Name == "Other" && finding.Resource.Selector is not null);
    }

    [Theory]
    [InlineData("CopyTo", "\"outputs\":{\"array\":\"new\"}", "out or ref")]
    [InlineData("Clear", "\"outputs\":{\"array\":\"new\"}", "out or ref")]
    [InlineData("CopyTo", "\"result\":\"new\"", "void")]
    [InlineData("Clear", "\"result\":\"sequence(elements(arg:array))\"", "needs an interface")]
    [InlineData("CopyTo", "\"result\":\"collection(elements(arg:array))\"", "needs an array")]
    [InlineData("CopyTo", "\"result\":\"dictionary(elements(arg:array),elements(arg:array))\"", "needs a Dictionary")]
    [InlineData("CopyTo", "\"result\":\"[arg:array]\"", "needs a member that returns something")]
    [InlineData("CopyTo", "\"fates\":{\"array\":{\"fate\":\"invoke-now\"}}", "not delegate-typed")]
    public async Task Clauses_the_projected_members_cannot_carry_are_rejected(string member, string form, string reason)
    {
        var solution = Solution("_state.Vector", "Array.Clear(source);");
        using var repo = new CellModelRepository(Models(member, "\"effects\":{}," + form));
        var files = ProjectModelFiles.Read(repo.Root);
        var (_, rejected) = ProjectModelResolver.Resolve(files, [(await solution.Projects.Single().GetCompilationAsync())!], ModelLock.Read(repo.Root, files));
        Assert.Contains(rejected, rejection => rejection.Reason.Contains(reason, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("_state.Vector", "keeps", true)]
    [InlineData("_state.Matrix", "keeps", true)]
    [InlineData("stoppingToken.IsCancellationRequested ? _state.Vector : _state.Matrix", "keeps", true)]
    [InlineData("new object[1]", "keeps", false)]
    [InlineData("_state.Vector", "stores", true)]
    [InlineData("_state.Matrix", "stores", true)]
    [InlineData("stoppingToken.IsCancellationRequested ? _state.Vector : _state.Matrix", "stores", true)]
    [InlineData("new object[1]", "stores", false)]
    public async Task Semantic_gap_feeding_agrees_with_the_direct_call_oracle(string receiver, string clause, bool feedsShared)
    {
        var form = clause == "keeps" ? "\"effects\":{},\"keeps\":{\"this\":[\"arg:array\"]}" :
            "\"effects\":{\"this\":[\"writes-cells\"]},\"stores\":{\"this\":[\"arg:array\"]}";
        using var repo = new CellModelRepository(Models("CopyTo", form));
        var direct = await PhaseOneAnalyzer.AnalyzeAsync(Solution(receiver, "source.CopyTo(Opaque.Get(), 0);"), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        var projected = await PhaseOneAnalyzer.AnalyzeAsync(Solution(receiver, "((ICollection)source).CopyTo(Opaque.Get(), 0);"), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        var expected = Assert.Single(direct.Coverage).Gaps.Select(gap => gap.Callee).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, Assert.Single(projected.Coverage).Gaps.Select(gap => gap.Callee).Order(StringComparer.Ordinal));
        Assert.Equal(feedsShared, expected.Any(callee => callee.Contains("Opaque.Get", StringComparison.Ordinal)));
    }

    private static string[] Accesses(EngineRun run) => run.Collection.Accesses.Where(access => !access.IsConstructionLocal)
        .Select(access => $"{access.Resource.Region}|{string.Join('.', access.Resource.AccessPath)}|{access.Operation}|{access.Symbol}|{access.Root.Display}|{string.Join(',', access.HeldProtection)}")
        .Distinct().Order(StringComparer.Ordinal).ToArray();

    private static string[] Gaps(EngineRun run) => run.Collection.Coverage.Gaps.Select(gap => gap.Callee).Order(StringComparer.Ordinal).ToArray();

    private static string Models(string member, string form)
    {
        var id = member == "Clear" ? "M:System.Array.Clear(System.Array)" : "M:System.Array.CopyTo(System.Array,System.Int32)";
        return $$$"""
            {"schemaVersion":1,"assemblies":["System.Private.CoreLib"],"models":[
              {"member":"M:System.Array.SetValue(System.Object,System.Int32)","effects":{},"keeps":{"this":["arg:value"]}},
              {"member":"{{{id}}}",{{{form}}}}
            ]}
            """;
    }

    private static Solution Solution(string receiver, string call) => FixtureSolution.Create(("Case.cs", Usings + $$"""
        using System.Collections;
        using System.Collections.Generic;
        public class Item { public int Hits; }
        public static class Opaque { public static extern Array Get(); }
        public class State
        {
            public readonly object[] Vector = new object[] { new Item() };
            public readonly object[,] Matrix = new object[,] { { new Item() } };
            public readonly object[] Target = new object[] { new Item() };
            public readonly object[] Other = new object[] { new Item() };
        }
        public class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                Array source = {{receiver}};
                source.SetValue(_state.Other, 0);
                _state.Target.SetValue(_state.Other, 0);
                {{call}}
                ((Item)_state.Target[0]).Hits = 1;
                return Task.CompletedTask;
            }
        }
        public class Writer(State state) : BackgroundService
        {
            private readonly State _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _state.Other[0] = new Item();
                ((Item)_state.Other[0]).Hits = 2;
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Writer>();")));
}
