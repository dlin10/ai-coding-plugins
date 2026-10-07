using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

/// <summary>How the lowering reads <c>.Result</c> and <c>GetResult()</c> (R3): a <see cref="IrJoinKind.Result"/> join on the task, whose
/// call keeps defining its own result, throwing only after completion on a <c>Task</c> and possibly before it on a <c>ValueTask</c>.</summary>
public sealed class ResultJoinLoweringTests
{
    [Fact]
    public async Task Task_result_is_a_result_join_on_the_task_that_throws_only_after_completion()
    {
        var (body, join) = await Single("int M(Task<int> t) => t.Result;");

        Assert.True(join.ThrowsOnlyAfterCompletion);
        Assert.Equal(Parameter(body, 0), Task(body, join));
        Assert.NotNull(Call(body, join).ResultValue);
    }

    [Fact]
    public async Task Value_task_result_is_a_result_join_that_may_throw_before_completion()
    {
        var (body, join) = await Single("int M(ValueTask<int> t) => t.Result;");

        Assert.False(join.ThrowsOnlyAfterCompletion);
        Assert.Equal(Parameter(body, 0), Task(body, join));
        Assert.NotNull(Call(body, join).ResultValue);
    }

    [Fact]
    public async Task Get_result_of_a_task_awaiter_is_a_result_join_on_the_task_behind_the_awaiter()
    {
        var (body, join) = await Single("int M(Task<int> t) => t.GetAwaiter().GetResult();");

        Assert.True(join.ThrowsOnlyAfterCompletion);
        Assert.Equal(Parameter(body, 0), Task(body, join));
        Assert.NotNull(Call(body, join).ResultValue);
    }

    [Fact]
    public async Task Get_result_through_a_stored_awaiter_joins_the_task_it_was_taken_from()
    {
        var (body, join) = await Single("int M(Task<int> t) { var awaiter = t.GetAwaiter(); return awaiter.GetResult(); }");

        Assert.True(join.ThrowsOnlyAfterCompletion);
        Assert.Equal(Parameter(body, 0), Task(body, join));
    }

    [Fact]
    public async Task Get_result_through_configure_await_joins_the_task_and_throws_only_after_completion()
    {
        var (body, join) = await Single("int M(Task<int> t) => t.ConfigureAwait(false).GetAwaiter().GetResult();");

        Assert.True(join.ThrowsOnlyAfterCompletion);
        Assert.Equal(Parameter(body, 0), Task(body, join));
        Assert.NotNull(Call(body, join).ResultValue);
    }

    [Fact]
    public async Task Get_result_of_a_non_generic_task_awaiter_is_a_join_that_defines_no_value()
    {
        var (body, join) = await Single("void M(Task t) => t.GetAwaiter().GetResult();");

        Assert.True(join.ThrowsOnlyAfterCompletion);
        Assert.Equal(Parameter(body, 0), Task(body, join));
        Assert.Null(Call(body, join).ResultValue);
        Assert.Empty(join.DefinedValues);
    }

    [Fact]
    public async Task Get_result_of_a_non_generic_value_task_awaiter_may_throw_before_completion_and_defines_no_value()
    {
        var (body, join) = await Single("void M(ValueTask t) => t.GetAwaiter().GetResult();");

        Assert.False(join.ThrowsOnlyAfterCompletion);
        Assert.Equal(Parameter(body, 0), Task(body, join));
        Assert.Null(Call(body, join).ResultValue);
    }

    [Fact]
    public async Task Get_result_of_a_configured_value_task_awaiter_may_throw_before_completion()
    {
        var (body, join) = await Single("void M(ValueTask t) => t.ConfigureAwait(false).GetAwaiter().GetResult();");

        Assert.False(join.ThrowsOnlyAfterCompletion);
        Assert.Equal(Parameter(body, 0), Task(body, join));
        Assert.Null(Call(body, join).ResultValue);
    }

    [Fact]
    public async Task Get_result_of_a_generic_value_task_awaiter_gives_a_value_and_may_throw_before_completion()
    {
        var (body, join) = await Single("int M(ValueTask<int> t) => t.ConfigureAwait(false).GetAwaiter().GetResult();");

        Assert.False(join.ThrowsOnlyAfterCompletion);
        Assert.Equal(Parameter(body, 0), Task(body, join));
        Assert.NotNull(Call(body, join).ResultValue);
    }

    [Fact]
    public async Task Get_result_of_a_configured_non_generic_task_awaiter_throws_only_after_completion()
    {
        var (body, join) = await Single("void M(Task t) => t.ConfigureAwait(false).GetAwaiter().GetResult();");

        Assert.True(join.ThrowsOnlyAfterCompletion);
        Assert.Equal(Parameter(body, 0), Task(body, join));
    }

    [Fact]
    public async Task Awaiter_members_other_than_get_result_are_no_joins()
    {
        var body = await Lower("void M(Task<int> t) { var awaiter = t.GetAwaiter(); var done = awaiter.IsCompleted; awaiter.OnCompleted(() => { }); }");

        Assert.Empty(Operations<IrJoinOperation>(body));
    }

    // ---- helpers ----

    /// <summary>The one join of a member, which has to be a <see cref="IrJoinKind.Result"/> join on one known handle.</summary>
    /// <param name="member">The member <c>M</c>.</param>
    private static async Task<(IrBody Body, IrJoinOperation Join)> Single(string member)
    {
        var body = await Lower(member);
        var join = Assert.Single(Operations<IrJoinOperation>(body));
        Assert.Equal(IrJoinKind.Result, join.Kind);
        Assert.True(join.HandlesKnown);
        Assert.Single(join.HandleValues);
        return (body, join);
    }

    private static int Parameter(IrBody body, int ordinal) => body.Parameters.Single(parameter => parameter.Ordinal == ordinal).Value;

    private static IrCallOperation Call(IrBody body, IrJoinOperation join) =>
        Operations<IrCallOperation>(body).Single(call => call.Id == join.CallOperationId);

    /// <summary>The value the join's handle is the same task as: followed back over assignments, conversions and the values that are the
    /// same task as another (<see cref="IrTaskKind.Same"/>).</summary>
    /// <param name="body">The lowered body.</param>
    /// <param name="join">The join.</param>
    private static int Task(IrBody body, IrJoinOperation join)
    {
        var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
        var value = join.HandleValues[0];
        for (var steps = 0; steps < operations.Length; steps++)
        {
            int? source = null;
            foreach (var operation in operations)
            {
                source ??= operation switch
                {
                    IrTaskOperation { Kind: IrTaskKind.Same, ResultValue: int same, TaskValue: int task } when same == value => task,
                    IrAssignOperation assign when assign.TargetValue == value => assign.SourceValue,
                    IrConvertOperation convert when convert.ResultValue == value => convert.OperandValue,
                    _ => null
                };
            }

            if (source is not int next)
                return value;
            value = next;
        }

        return value;
    }

    private static T[] Operations<T>(IrBody body) where T : IrOperation =>
        body.Blocks.SelectMany(block => block.Operations).OfType<T>().ToArray();

    private static async Task<IrBody> Lower(string member)
    {
        var source = $$"""
            using System;
            using System.Threading.Tasks;

            class C
            {
                {{member}}
            }
            """;
        var solution = FixtureSolution.Create(("Case.cs", source));
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var method = compilation.GetTypeByMetadataName("C")!.GetMembers("M").OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(method, compilation, @"C:\fixture", CancellationToken.None).Body;
    }
}
