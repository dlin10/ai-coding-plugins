using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of task composition (issue 506), each with what run time calls for. <c>Task.WhenEach</c> yields each task
/// as it completes ("Creates an IAsyncEnumerable that will yield the supplied tasks as those tasks complete"), so a loop that breaks
/// leaves the rest running and one enumerated to the end has seen every task complete (both inferred from that sentence; Learn
/// says nothing of early exit). A child started with <c>AttachedToParent</c> inside <c>Task.Run</c> is detached, since <c>Task.Run</c>
/// creates its task with <c>DenyChildAttach</c>, while a parent started with <c>Task.Factory.StartNew</c> waits for its attached
/// children, and so does a wait on it ("Attached and detached child tasks"). Every probe runs through the whole analysis
/// (<see cref="HostedServiceProbe"/>) and names each finding's pair of sites, the child's and the parent's writes marked apart; the
/// second hosted service does nothing, so every pair is within the first one's work. A probe the analysis answers otherwise is named
/// in <see cref="Differences"/> with its answer and why.</summary>
public sealed class TaskCompositionProbeTests
{
    /// <summary>Why a <c>Task.WhenEach</c> loop run to its end orders nothing.</summary>
    private const string WHEN_EACH_IS_NO_JOIN =
        "issue 514: a Task.WhenEach loop that runs to its end has awaited every task it was given, but the analysis reads no join " +
        "there, so the work of those tasks pairs with what follows the loop";

    /// <summary>Why a parent's wait does not order its attached child.</summary>
    private const string ATTACHMENT_IS_NOT_MODELLED =
        "limit: no edge of SPEC 3.3 joins an attached child to its parent, so a wait on a parent started by Task.Factory.StartNew " +
        "orders nothing its AttachedToParent children do";

    /// <summary>The race of the waited work's write with the write after the loop.</summary>
    private const string WORK_AGAINST_AFTER = "DCA1001 unprotected _seen: Write Holder.RunAsync | Write Holder.WorkAsync";

    /// <summary>The race of the child's write with the parent's later write, both written in <c>RunAsync</c>.</summary>
    private const string CHILD_AGAINST_AFTER = "DCA1001 unprotected _x: Write Holder.RunAsync @after | Write Holder.RunAsync @child";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["when-each-left-early-leaves-a-task-running"] = (Holder("_seen", "private async Task WorkAsync() { await Task.Yield(); _seen = 1; }",
            "public async Task RunAsync() { var slow = WorkAsync(); await foreach (var t in Task.WhenEach(new[] { Task.CompletedTask, slow })) break; _seen = 0; }"),
            [WORK_AGAINST_AFTER]),
        ["when-each-enumerated-to-the-end"] = (Holder("_seen", "private async Task WorkAsync() { await Task.Yield(); _seen = 1; }",
            "public async Task RunAsync() { var slow = WorkAsync(); await foreach (var t in Task.WhenEach(new[] { Task.CompletedTask, slow })) { } _seen = 0; }"),
            []),
        ["attached-child-of-task-run"] = (Holder("_x", string.Empty,
            "public async Task RunAsync() { await Task.Run(() => { Task.Factory.StartNew(() => /*@child*/_x = 1, TaskCreationOptions.AttachedToParent); }); /*@after*/_x = 2; }"),
            [CHILD_AGAINST_AFTER]),
        ["attached-child-of-a-waited-start-new"] = (Holder("_x", string.Empty,
            "public async Task RunAsync() { Task.Factory.StartNew(() => { Task.Factory.StartNew(() => /*@child*/_x = 1, TaskCreationOptions.AttachedToParent); }).Wait(); " +
            "/*@after*/_x = 2; await Task.CompletedTask; }"),
            [])
    };

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["when-each-enumerated-to-the-end"] = new([WORK_AGAINST_AFTER], WHEN_EACH_IS_NO_JOIN),
        ["attached-child-of-a-waited-start-new"] = new([CHILD_AGAINST_AFTER], ATTACHMENT_IS_NOT_MODELLED)
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The probe's findings are what run time calls for, or the answer its difference pins, read as sets.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Probe_answers_as_run_time_calls_for_or_as_its_named_difference(string name)
    {
        var (probe, truth) = Probes[name];

        var findings = await probe.FindingsAsync();

        HostedServiceProbe.AssertAnswer(findings, truth, Differences.GetValueOrDefault(name));
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);

    /// <summary>A probe of a <c>Holder</c> with one <c>int</c> field whose <c>RunAsync</c> the first hosted service starts without
    /// awaiting it; the second does nothing.</summary>
    /// <param name="field">The name of the holder's <c>int</c> field.</param>
    /// <param name="helper">A member <c>RunAsync</c> uses, or nothing.</param>
    /// <param name="run">The declaration of <c>RunAsync</c>.</param>
    private static HostedServiceProbe Holder(string field, string helper, string run) => new("Holder", $$"""
        public sealed class Holder
        {
            private int {{field}};
            {{helper}}
            {{run}}
        }
        """, "_ = _holder.RunAsync();", "_ = 0;");
}
