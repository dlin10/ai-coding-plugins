using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

/// <summary>The phase 5d cases of a field-like event (question 62, R11): the handler a subscription stores in the event's field runs
/// where the event is raised, so its accesses are there to pair or to stay apart.</summary>
public sealed class DemoFieldLikeEventTests
{
    [Fact]
    public async Task Handler_increment_runs_in_the_worker_that_raises_and_pairs_with_the_action_read()
    {
        var result = await DemoExpectationTests.SharedDemo.Value;
        const string CASE = $"{CASES}FieldLikeEventRaisedByWorker.";

        var ticks = OnTicks(result.Accesses, $"di:{CASE}TickCounter@Singleton");
        var increment = Assert.Single(ticks, access => access.Symbol == $"{CASE}TickCounter.OnTick()" &&
                                                       access.Operation == AccessOperation.ReadModifyWrite &&
                                                       access.Root.Symbol == $"{CASE}TickWorker.ExecuteAsync(CancellationToken)");
        var read = Assert.Single(ticks, access => access.Symbol == $"{CASE}TickCounter.Read()" && access.Operation == AccessOperation.Read &&
                                                  access.Root.Symbol == $"{CASE}TickController.Get()");
        var finding = Assert.Single(result.Findings, finding => finding.Resource.Region == $"di:{CASE}TickCounter@Singleton");
        Assert.Equal(new[] { increment, read }.Select(Describe).Order(StringComparer.Ordinal),
                     new[] { finding.AccessA, finding.AccessB }.Select(Describe).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Handler_increment_and_read_run_in_the_request_that_owns_the_objects_and_do_not_pair()
    {
        var result = await DemoExpectationTests.SharedDemo.Value;
        const string CASE = $"{CASES}FieldLikeEventRaisedInRequest.";

        var ticks = OnTicks(result.Accesses, $"di:{CASE}TickCounter@Scoped");
        var increment = Assert.Single(ticks, access => access.Symbol == $"{CASE}TickCounter.OnTick()" &&
                                                       access.Operation == AccessOperation.ReadModifyWrite);
        var read = Assert.Single(ticks, access => access.Symbol == $"{CASE}TickCounter.Read()" && access.Operation == AccessOperation.Read);
        Assert.Equal($"{CASE}TickController.Get()", increment.Root.Symbol);
        Assert.Equal($"{CASE}TickController.Get()", read.Root.Symbol);
        Assert.Equal(increment.ExecutionId, read.ExecutionId);
        Assert.DoesNotContain(result.Findings, finding => finding.Resource.Region.Contains(CASE, StringComparison.Ordinal) ||
                                                          finding.AccessA.Symbol.StartsWith(CASE, StringComparison.Ordinal) ||
                                                          finding.AccessB.Symbol.StartsWith(CASE, StringComparison.Ordinal));
    }

    private const string CASES = "Demo.Web.Cases.";

    private static Access[] OnTicks(IReadOnlyList<Access> accesses, string region) =>
        accesses.Where(access => access.Resource.Region == region && access.Resource.Member.Name == "_ticks" && !access.IsConstructionLocal)
                .ToArray();

    private static string Describe(Access access) => $"{access.Symbol}|{access.Operation}|{access.Root.Symbol}";
}
