using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class PatternLoweringTests
{
    [Fact]
    public async Task Declaration_pattern_binds_the_tested_object() =>
        OnSlot(await Run("if ((object)slot is Slot s) s.Count++;"));

    [Fact]
    public async Task Var_pattern_binds_the_tested_value() =>
        OnSlot(await Run("if (slot is var s) s.Count++;"));

    [Fact]
    public async Task Recursive_pattern_designation_binds_the_tested_object() =>
        OnSlot(await Run("if (slot is Slot { Count: >= 0 } s) s.Count++;"));

    [Fact]
    public async Task Nested_designation_binds_the_member_value()
    {
        var result = await Run("if (slot is { Inner: Slot inner }) inner.Count++;", "public Slot Inner = new();");
        Assert.Contains(result.Accesses, access => access.Resource.Region == "alloc:Slot..ctor()#Slot" &&
                                                  access.Resource.AccessPath.SequenceEqual(["Count"]));
    }

    [Fact]
    public async Task Property_subpattern_reads_the_member_on_the_tested_object() =>
        OnSlot(await Run("_ = slot is { Count: > 0 };"));

    [Fact]
    public async Task Property_subpattern_of_the_same_type_does_not_read_this()
    {
        var result = await Run("_ = slot is { Count: > 0 };", controllerMembers: """
            public int Count;
            public void WriteThis() => Count++;
            public void WriteSlot([FromServices] Slot slot) => slot.Count++;
            """);
        Assert.Contains(result.Findings, finding => finding.Resource.Region == "di:Slot@Singleton" &&
                                                   new[] { finding.AccessA.Symbol, finding.AccessB.Symbol }.Contains("CaseController.Post(Slot)"));
        Assert.DoesNotContain(result.Accesses, access => access.Symbol == "CaseController.Post(Slot)" &&
                                                     access.Resource.Region.Contains("CaseController", StringComparison.Ordinal) &&
                                                     access.Resource.AccessPath.SequenceEqual(["Count"]));
    }

    [Fact]
    public async Task Property_subpattern_in_a_static_method_reads_the_tested_object() =>
        OnSlot(await Run("_ = Check(slot);", controllerMembers: "private static bool Check(Slot value) => value is { Count: > 0 };"));

    [Fact]
    public async Task Property_subpattern_getter_runs_on_the_tested_object() =>
        OnSlot(await Run("_ = slot is { Level: > 0 };", "public int Level { get { Count++; return Count; } }"));

    [Fact]
    public async Task Extended_property_subpattern_reads_each_member_on_its_own_object()
    {
        var result = await Run("_ = slot is { Inner.Count: > 0 };", "public Slot Inner = new();");
        Assert.Contains(result.Accesses, access => access.Resource.Region == "alloc:Slot..ctor()#Slot" &&
                                                  access.Resource.AccessPath.SequenceEqual(["Count"]));
        Assert.DoesNotContain(result.Accesses, access => access.Resource.Region == "di:Slot@Singleton" &&
                                                     access.Resource.AccessPath.SequenceEqual(["Count"]));
    }

    [Fact]
    public async Task Switch_statement_pattern_binds() =>
        OnSlot(await Run("switch ((object)slot) { case Slot s: s.Count++; break; }"));

    [Fact]
    public async Task Switch_expression_arm_pattern_binds() =>
        OnSlot(await Run("_ = ((object)slot) switch { Slot s => s.Count++, _ => 0 };"));

    [Fact]
    public async Task Null_constant_and_relational_patterns_lower_as_before()
    {
        var result = await Run("_ = slot is not null; _ = slot.Count is > 0 and < 10; _ = slot.Count is 3;");
        Assert.Contains(result.Accesses, access => access.Resource.Region == "di:Slot@Singleton" &&
                                                  access.Resource.AccessPath.SequenceEqual(["Count"]));
    }

    [Fact]
    public async Task Pattern_forms_of_this_task_leave_no_unsupported_operation()
    {
        var result = await Run("if (slot is Slot { Count: > 0 } s) s.Count++; switch ((object)slot) { case Slot x: x.Count++; break; }");
        Assert.Equal(0, result.Coverage[0].Skips[CoverageCounters.UNSUPPORTED_OPERATION]);
    }

    private static async Task<AnalysisResult> Run(string action, string slotMembers = "", string controllerMembers = "") =>
        await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + $$"""
            public sealed class Slot { public int Count; {{slotMembers}} }
            public sealed class CaseController : ControllerBase
            {
                {{controllerMembers}}
                public void Post([FromServices] Slot slot) { {{action}} }
            }
            """ + Startup("services.AddSingleton<Slot>();"))), ROOT_DIRECTORY, CancellationToken.None);

    private static void OnSlot(AnalysisResult result) =>
        Assert.Contains(result.Accesses, access => access.Resource.Region == "di:Slot@Singleton" &&
                                                  access.Resource.AccessPath.SequenceEqual(["Count"]));
}
