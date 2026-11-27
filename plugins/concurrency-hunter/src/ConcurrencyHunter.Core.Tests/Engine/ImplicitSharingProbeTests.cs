using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of state shared without a field the code declares for it, or reached by a route other than a field
/// (issue 506), each with what run time calls for. A primary-constructor parameter that a member uses is captured as a field of the
/// object ("Primary constructors"), so a singleton that increments or reassigns it shares it with every caller. A finalizer runs on the
/// finalizer thread, concurrently with the program's own threads, so one that changes a shared static races with the program writing
/// it. An object, an array or a list a method hands to work that runs concurrently with it — an async call it does not await yet, a
/// lambda <c>Task.Run</c> runs — is shared with that work. Three routes are probed apart, each beside a control the analysis answers
/// right: the cells of an array an async method gets as a parameter, after the method's first <c>await</c>; the cells of an array the
/// method created and handed to a lambda, as the creating method reads or writes them; and a list no field and no capture cell holds,
/// in the creator and in the callee. For those, <see cref="AccessClaims"/> says which access was collected and which was not, by the
/// marked site, beside the findings. Every probe runs through the whole analysis (<see cref="HostedServiceProbe"/>) and names each
/// finding's pair of sites; a probe the analysis answers otherwise is named in <see cref="Differences"/> with its answer and why.</summary>
public sealed class ImplicitSharingProbeTests
{
    /// <summary>Why a finalizer's accesses never pair.</summary>
    private const string FINALIZER_IS_NO_ROOT =
        "limit: no root provider gives a finalizer an execution, and SPEC names no finalizer thread, so a finalizer's accesses are " +
        "never collected";

    /// <summary>Why the creator's own accesses to an array it handed to a lambda are lost.</summary>
    private const string FRESH_ARRAY_CREATOR_ACCESSES =
        "issue 517: the method that creates an array and hands it to a lambda Task.Run runs makes no " +
        "access to its cells, read or written, while the lambda's access through the capture cell is kept; an object or a list handed " +
        "the same way pairs";

    /// <summary>Why a list no field and no capture cell holds makes no access.</summary>
    private const string COLLECTION_WITHOUT_HOLDER =
        "issue 516: a recognized collection member's effects become accesses only on a collection some " +
        "field or capture cell holds (InterproceduralAccesses.StandingMemberAccesses skips one with no holder: the collection-resource " +
        "boundary of TD-043 and ADR 0010), so a list held by a local of the creator and a parameter of the callee makes no access on " +
        "either side, even where the creator shares it with an async call it has not awaited";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["primary-constructor-parameter-incremented"] = (Counter("public sealed class Counter(Tally tally, int start = 0) { public int Next() => ++start + tally.Value; }"),
                                                          ["DCA1002 unprotected start: ReadModifyWrite Counter.Next | ReadModifyWrite Counter.Next"]),
        ["primary-constructor-parameter-reassigned"] = (Counter("public sealed class Counter(Tally tally) { public int Next() { tally = new Tally(); return tally.Value; } }"),
                                                        ["DCA1001 unprotected tally: Read Counter.Next | Write Counter.Next",
                                                         "DCA1001 unprotected tally: Write Counter.Next | Write Counter.Next"]),
        ["primary-constructor-parameter-copied-to-a-readonly-field"] = (Counter(
            "public sealed class Counter(Tally tally) { private readonly Tally _tally = tally; public int Next() => _tally.Value + 1; }"), []),
        ["finalizer-decrements-a-shared-static"] = (new("Factory", """
            public static class Live { public static int Count; }
            public sealed class Native { ~Native() { Live.Count--; } }
            public sealed class Factory { public void Make() { _ = new Native(); } public void Bump() => Live.Count++; }
            """, "_factory.Make();", "_factory.Bump();"), ["DCA1002 unprotected Count: ReadModifyWrite Factory.Bump | ReadModifyWrite Native.Finalize"]),
        ["object-handed-to-an-async-call-not-yet-awaited"] = (Sender(
            "private static async Task FillAsync(Box box) { await Task.Yield(); box.Value = 1; }",
            "var box = new Box(); var fill = FillAsync(box); box.Value = 2; await fill;"),
            ["DCA1001 unprotected Value: Write Sender.FillAsync | Write Sender.RunAsync"]),
        ["object-captured-by-task-run"] = (Sender(string.Empty,
            "var box = new Box(); var fill = Task.Run(() => /*@lambda*/box.Value = 1); /*@creator*/box.Value = 2; await fill;"),
            ["DCA1001 unprotected Value: Write Sender.RunAsync @creator | Write Sender.RunAsync @lambda"]),
        ["array-field-written-by-an-async-callee-before-its-await"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { /*@callee*/cells[0] = 1; await Task.Yield(); }", "await FillAsync(_cells);"),
            ["DCA1001 unprotected _cells.[0]: Write Sender.FillAsync @callee | Write Sender.Touch @touch"]),
        ["array-field-written-by-an-async-callee-after-its-await"] = (Toucher(
            "private static async Task FillAsync(int[] cells) { await Task.Yield(); /*@callee*/cells[0] = 1; }", "await FillAsync(_cells);"),
            ["DCA1001 unprotected _cells.[0]: Write Sender.FillAsync @callee | Write Sender.Touch @touch"]),
        ["array-field-read-by-an-async-callee-after-its-await"] = (Toucher(
            "public int Seen; private async Task ReadAsync(int[] cells) { await Task.Yield(); var seen = /*@callee*/cells[0]; Seen = seen; }",
            "await ReadAsync(_cells);"),
            ["DCA1001 unprotected _cells.[0]: Read Sender.ReadAsync @callee | Write Sender.Touch @touch"]),
        ["list-field-added-to-by-an-async-callee-after-its-await"] = (new("Sender", """
            public sealed class Sender
            {
                private readonly List<int> _items = new();
                private static async Task FillAsync(List<int> items) { await Task.Yield(); /*@callee*/items.Add(1); }
                public async Task RunAsync() { await FillAsync(_items); }
                public void Touch() => /*@touch*/_items.Add(2);
            }
            """, "_ = _sender.RunAsync();", "_sender.Touch();"),
            ["DCA1001 unprotected _items structure: Write Sender.FillAsync @callee | Write Sender.Touch @touch",
             "DCA1001 unprotected _items.[?]: Write Sender.FillAsync @callee | Write Sender.Touch @touch"]),
        ["array-captured-by-task-run-read-by-the-lambda-written-by-its-creator"] = (Sender("public int Seen;",
            "var cells = new int[1]; var fill = Task.Run(() => { var seen = /*@lambda*/cells[0]; Seen = seen; }); /*@creator*/cells[0] = 2; await fill;"),
            ["DCA1001 unprotected cells.[0]: Read Sender.RunAsync @lambda | Write Sender.RunAsync @creator"]),
        ["array-captured-by-task-run-written-by-the-lambda-read-by-its-creator"] = (Sender("public int Seen;",
            "var cells = new int[1]; var fill = Task.Run(() => /*@lambda*/cells[0] = 1); var seen = /*@creator*/cells[0]; Seen = seen; await fill;"),
            ["DCA1001 unprotected cells.[0]: Read Sender.RunAsync @creator | Write Sender.RunAsync @lambda"]),
        ["list-captured-by-task-run"] = (Sender(string.Empty,
            "var items = new List<int>(); var fill = Task.Run(() => /*@lambda*/items.Add(1)); /*@creator*/items.Add(2); await fill;"),
            ["DCA1001 unprotected items structure: Write Sender.RunAsync @creator | Write Sender.RunAsync @lambda",
             "DCA1001 unprotected items.[?]: Write Sender.RunAsync @creator | Write Sender.RunAsync @lambda"]),
        ["list-handed-to-an-async-call-not-yet-awaited"] = (Sender(
            "private static async Task FillAsync(List<int> items) { await Task.Yield(); /*@callee*/items.Add(1); }",
            "var items = new List<int>(); var fill = FillAsync(items); /*@creator*/items.Add(2); await fill;"),
            [$"DCA1001 unprotected {HostedServiceProbe.NO_FIELD} structure: Write Sender.FillAsync @callee | Write Sender.RunAsync @creator",
             $"DCA1001 unprotected {HostedServiceProbe.NO_FIELD}.[?]: Write Sender.FillAsync @callee | Write Sender.RunAsync @creator"])
    };

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["finalizer-decrements-a-shared-static"] = new([], FINALIZER_IS_NO_ROOT),
        ["array-captured-by-task-run-read-by-the-lambda-written-by-its-creator"] = new([], FRESH_ARRAY_CREATOR_ACCESSES),
        ["array-captured-by-task-run-written-by-the-lambda-read-by-its-creator"] = new([], FRESH_ARRAY_CREATOR_ACCESSES),
        ["list-handed-to-an-async-call-not-yet-awaited"] = new([], COLLECTION_WITHOUT_HOLDER)
    };

    /// <summary>For each probe of a route an access can be lost on: the accesses that were collected, as
    /// <see cref="HostedServiceProbe.DescribeAccess"/> writes them, and the resources at marked sites that have none, as
    /// <c>resource @marker</c>, <c>*</c> standing for any resource.</summary>
    private static readonly Dictionary<string, (string[] Collected, string[] Lost)> AccessClaims = new(StringComparer.Ordinal)
    {
        ["array-field-written-by-an-async-callee-after-its-await"] = (["Write _cells.[0] Sender.FillAsync @callee", "Write _cells.[0] Sender.Touch @touch"], []),
        ["array-field-read-by-an-async-callee-after-its-await"] = (["Read _cells.[0] Sender.ReadAsync @callee", "Write _cells.[0] Sender.Touch @touch"], []),
        ["array-captured-by-task-run-read-by-the-lambda-written-by-its-creator"] = (["Read cells.[0] Sender.RunAsync @lambda"], ["cells.[0] @creator"]),
        ["array-captured-by-task-run-written-by-the-lambda-read-by-its-creator"] = (["Write cells.[0] Sender.RunAsync @lambda"], ["cells.[0] @creator"]),
        ["list-handed-to-an-async-call-not-yet-awaited"] = ([], ["* @callee", "* @creator"])
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The names of the probes with a claim about accesses.</summary>
    public static TheoryData<string> AccessClaimNames() => new(AccessClaims.Keys);

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

    /// <summary>The probe collected the accesses its claim names and none on the resources it says were lost at their marked sites.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(AccessClaimNames))]
    public async Task Probe_collects_the_accesses_its_claim_names(string name)
    {
        var (collected, lost) = AccessClaims[name];

        var answer = await Probes[name].Probe.AnalyzeAsync(new ProbeInputs());

        HostedServiceProbe.AssertAccessClaims(answer, collected, lost);
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);

    /// <summary>A probe of a singleton <c>Counter</c> taking a <c>Tally</c>, whose <c>Next</c> both hosted services call.</summary>
    /// <param name="types">The declaration of <c>Counter</c>.</param>
    private static HostedServiceProbe Counter(string types) =>
        new("Counter", types, "_ = _counter.Next();", "_ = _counter.Next();", "services.AddSingleton<Tally>();");

    /// <summary>A probe of a <c>Sender</c> whose <c>RunAsync</c> the first hosted service starts; the second does nothing.</summary>
    /// <param name="helper">A member <c>RunAsync</c> calls, or nothing.</param>
    /// <param name="body">The body of <c>RunAsync</c>.</param>
    private static HostedServiceProbe Sender(string helper, string body) => new("Sender", $$"""
        public sealed class Sender
        {
            {{helper}}
            public async Task RunAsync() { {{body}} }
        }
        """, "_ = _sender.RunAsync();", "_ = 0;");

    /// <summary>A probe of a <c>Sender</c> holding an array in <c>_cells</c>, whose <c>RunAsync</c> hands it to an async callee while the
    /// second hosted service writes its first cell in <c>Touch</c>, at the site marked <c>touch</c>.</summary>
    /// <param name="callee">The async callee and what it needs.</param>
    /// <param name="call">The body of <c>RunAsync</c>.</param>
    private static HostedServiceProbe Toucher(string callee, string call) => new("Sender", $$"""
        public sealed class Sender
        {
            private readonly int[] _cells = new int[1];
            {{callee}}
            public async Task RunAsync() { {{call}} }
            public void Touch() => /*@touch*/_cells[0] = 2;
        }
        """, "_ = _sender.RunAsync();", "_sender.Touch();");
}
