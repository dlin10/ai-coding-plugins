using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of twists no test exercised (issue 506), each applied to a form the analysis already answers right — two
/// hosted services writing one field of a singleton's object, reported, or doing it under one lock, not — so that the twist is the
/// only new thing. A C# 14 extension member, a user-defined operator (C# 14 user-defined compound assignment and increment
/// included), a default interface method, a partial member and a member written in a file marked <c>&lt;auto-generated/&gt;</c> run
/// their bodies as any member does. A <c>using</c> declaration or statement disposes its resource when the scope ends, whatever
/// work the scope handed it to is still doing. A delegate a method invokes before it returns runs on the caller's thread, under the
/// locks the caller holds. An <c>await</c> whose awaiter is already complete does not suspend (the C# rule for <c>IsCompleted</c>), so
/// an async method that awaits only completed tasks runs to its end before its caller goes on. Every probe runs through the whole
/// analysis (<see cref="HostedServiceProbe"/>) and names each finding's pair of sides; a probe the analysis answers otherwise is named
/// in <see cref="Differences"/> with its answer and why.</summary>
public sealed class TwistProbeTests
{
    /// <summary>Why an async method that awaits only completed tasks overlaps its caller.</summary>
    private const string SYNCHRONOUS_COMPLETION_IS_NOT_MODELLED =
        "limit: every await suspends in the model, so what an async method does after awaiting a completed task runs, for the " +
        "analysis, concurrently with its caller";

    /// <summary>A meter whose static operator and partial method write its value.</summary>
    private const string METER = """
        public sealed partial class Meter
        {
            public int Value;
            public static Meter operator +(Meter meter, int amount) { meter.Value += amount; return meter; }
            public partial void Bump();
        }

        """;

    /// <summary>A meter whose C# 14 instance operators update its value in place.</summary>
    private const string METER_IN_PLACE = """
        public sealed class Meter
        {
            public int Value;
            public void operator +=(int amount) { Value += amount; }
            public void operator ++() { Value++; }
        }

        """;

    /// <summary>The part of <see cref="METER"/> that implements <c>Bump</c>, in the same file.</summary>
    private const string METER_BUMP = "public sealed partial class Meter { public partial void Bump() { Value++; } }\n";

    /// <summary>A resource whose <c>Dispose</c> closes it and whose <c>Write</c> counts while it is open.</summary>
    private const string RESOURCE = """
        public sealed class Resource : IDisposable
        {
            private bool _closed;
            public int Count;
            public void Write() { if (!_closed) Count++; }
            public void Dispose() => _closed = true;
        }

        """;

    /// <summary>The lost update of <c>Value</c> in two runs of <paramref name="member"/>.</summary>
    /// <param name="member">The member that increments it.</param>
    private static string ValueIncrementedIn(string member) => $"DCA1002 unprotected Value: ReadModifyWrite {member} | ReadModifyWrite {member}";

    /// <summary>The race of <c>Dispose</c>'s write of <c>_closed</c> with <c>Write</c>'s read of it.</summary>
    private const string CLOSED_WHILE_WRITING = "DCA1001 unprotected _closed: Read Resource.Write | Write Resource.Dispose";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them,
    /// and the further files of its project.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth, (string Path, string Source)[] Files)> Probes =
        new(StringComparer.Ordinal)
        {
            ["extension-block-method"] = (Both("""
                public static class BoxExtensions { extension(Box box) { public void Bump() => box.Value++; } }
                public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }
                """), [ValueIncrementedIn("BoxExtensions.Bump")], []),
            ["extension-block-property-setter"] = (Both("""
                public static class BoxExtensions { extension(Box box) { public int Doubled { get => box.Value * 2; set => box.Value = value / 2; } } }
                public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Doubled = 4; }
                """), ["DCA1001 unprotected Value: Write BoxExtensions.set_Doubled | Write BoxExtensions.set_Doubled"], []),
            ["classic-extension-method"] = (Both("""
                public static class BoxExtensions { public static void Bump(this Box box) => box.Value++; }
                public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }
                """), [ValueIncrementedIn("BoxExtensions.Bump")], []),
            ["user-defined-binary-operator"] = (Both(METER + METER_BUMP +
                "public sealed class Holder { private readonly Meter _meter = new(); public void Run() => _ = _meter + 1; }"),
                [ValueIncrementedIn("Meter.op_Addition")], []),
            ["user-defined-compound-assignment-operator"] = (Both(METER_IN_PLACE +
                "public sealed class Holder { private Meter _meter = new(); public void Run() => _meter += 1; }"),
                [ValueIncrementedIn("Meter.op_AdditionAssignment")], []),
            ["user-defined-increment-operator"] = (Both(METER_IN_PLACE +
                "public sealed class Holder { private Meter _meter = new(); public void Run() => _meter++; }"),
                [ValueIncrementedIn("Meter.op_IncrementAssignment")], []),
            ["default-interface-method"] = (Both("""
                public interface ICounter { int Count { get; set; } void Bump() => Count++; }
                public sealed class Counter : ICounter { public int Count { get; set; } }
                public sealed class Holder { private readonly ICounter _counter = new Counter(); public void Run() => _counter.Bump(); }
                """), ["DCA1002 unprotected Count: ReadModifyWrite Counter.set_Count | ReadModifyWrite Counter.set_Count"], []),
            ["partial-method-implemented-in-another-part"] = (Both(METER + METER_BUMP +
                "public sealed class Holder { private readonly Meter _meter = new(); public void Run() => _meter.Bump(); }"),
                [ValueIncrementedIn("Meter.Bump")], []),
            ["partial-property"] = (Both("""
                public sealed partial class Gauge { public partial int Level { get; set; } }
                public sealed partial class Gauge { private int _level; public partial int Level { get => _level; set => _level = value; } }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Run() => _gauge.Level = 1; }
                """), ["DCA1001 unprotected _level: Write Gauge.set_Level | Write Gauge.set_Level"], []),
            ["partial-property-against-its-field"] = (new("Holder", """
                public sealed partial class Gauge { public partial int Level { get; set; } }
                public sealed partial class Gauge { private int _level; public partial int Level { get => _level; set => _level = value; } public void Bump() => _level++; }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Set() => _gauge.Level = 1; public void Bump() => _gauge.Bump(); }
                """, "_holder.Set();", "_holder.Bump();"), ["DCA1002 unprotected _level: ReadModifyWrite Gauge.Bump | Write Gauge.set_Level"], []),
            ["partial-method-implemented-in-a-generated-file"] = (Both(METER +
                "public sealed class Holder { private readonly Meter _meter = new(); public void Run() => _meter.Bump(); }"),
                [ValueIncrementedIn("Meter.Bump")],
                [("Meter.g.cs", "// <auto-generated/>\npublic sealed partial class Meter { public partial void Bump() { Value++; } }\n")]),
            ["method-in-a-generated-file"] = (Both(METER + METER_BUMP +
                "public sealed class Holder { private readonly Meter _meter = new(); public void Run() => MeterTools.Bump(_meter); }"),
                [ValueIncrementedIn("MeterTools.Bump")],
                [("MeterTools.g.cs", "// <auto-generated/>\npublic static class MeterTools { public static void Bump(Meter meter) { meter.Value++; } }\n")]),
            ["using-declaration-disposed-while-a-task-uses-it"] = (First(RESOURCE +
                "public sealed class Holder { public void Run() { using var resource = new Resource(); _ = Task.Run(() => resource.Write()); } }",
                "_holder.Run();"), [CLOSED_WHILE_WRITING], []),
            ["using-declaration-disposed-after-the-task-is-waited"] = (First(RESOURCE +
                "public sealed class Holder { public void Run() { using var resource = new Resource(); Task.Run(() => resource.Write()).Wait(); } }",
                "_holder.Run();"), [], []),
            ["using-statement-ends-while-an-async-call-uses-it"] = (First(RESOURCE + """
                public sealed class Holder
                {
                    private static async Task WriteLaterAsync(Resource resource) { await Task.Yield(); resource.Write(); }
                    public void Run() { using (var resource = new Resource()) { _ = WriteLaterAsync(resource); } }
                }
                """, "_holder.Run();"), [CLOSED_WHILE_WRITING], []),
            ["using-statement-ends-after-the-async-call-is-awaited"] = (First(RESOURCE + """
                public sealed class Holder
                {
                    private static async Task WriteLaterAsync(Resource resource) { await Task.Yield(); resource.Write(); }
                    public async Task RunAsync() { using (var resource = new Resource()) { await WriteLaterAsync(resource); } }
                }
                """, "_ = _holder.RunAsync();"), [], []),
            ["callback-invoked-inline-under-the-callers-lock"] = (new("Holder", """
                public sealed class Holder
                {
                    private readonly object _gate = new();
                    private int _total;
                    private static void Apply(Action action) => action();
                    public void Run() { lock (_gate) Apply(() => _total++); }
                    public void Reset() { lock (_gate) _total = 0; }
                }
                """, "_holder.Run();", "_holder.Reset();"), [], []),
            ["list-for-each-callback-under-the-callers-lock"] = (new("Holder", """
                public sealed class Holder
                {
                    private readonly object _gate = new();
                    private readonly List<int> _items = new() { 1 };
                    private int _total;
                    public void Run() { lock (_gate) _items.ForEach(x => _total += x); }
                    public void Reset() { lock (_gate) _total = 0; }
                }
                """, "_holder.Run();", "_holder.Reset();"), [], []),
            ["await-of-tasks-that-completed-synchronously"] = (First("""
                public sealed class Holder
                {
                    private int _x;
                    private async Task WorkAsync() { await Task.CompletedTask; _x = 1; }
                    public async Task RunAsync() { var work = WorkAsync(); _x = 2; await work; }
                }
                """, "_ = _holder.RunAsync();"), [], []),
            ["await-of-task-yield"] = (First("""
                public sealed class Holder
                {
                    private int _x;
                    private async Task WorkAsync() { await Task.Yield(); _x = 1; }
                    public async Task RunAsync() { var work = WorkAsync(); _x = 2; await work; }
                }
                """, "_ = _holder.RunAsync();"), ["DCA1001 unprotected _x: Write Holder.RunAsync | Write Holder.WorkAsync"], [])
        };

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["await-of-tasks-that-completed-synchronously"] = new(["DCA1001 unprotected _x: Write Holder.RunAsync | Write Holder.WorkAsync"],
                                                              SYNCHRONOUS_COMPLETION_IS_NOT_MODELLED)
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The probe's findings are what run time calls for, or the answer its difference pins, read as sets.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Probe_answers_as_run_time_calls_for_or_as_its_named_difference(string name)
    {
        var (probe, truth, files) = Probes[name];

        var answer = await probe.AnalyzeAsync(new ProbeInputs(files, [], false));

        HostedServiceProbe.AssertAnswer(answer.Findings, truth, Differences.GetValueOrDefault(name));
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);

    /// <summary>A probe of a singleton <c>Holder</c> whose <c>Run</c> both hosted services call.</summary>
    /// <param name="types">The declaration of <c>Holder</c> and the types it uses.</param>
    private static HostedServiceProbe Both(string types) => new("Holder", types, "_holder.Run();", "_holder.Run();");

    /// <summary>A probe of a singleton <c>Holder</c> on which the first hosted service runs one statement; the second does nothing.</summary>
    /// <param name="types">The declaration of <c>Holder</c> and the types it uses.</param>
    /// <param name="statement">The statement the first hosted service runs.</param>
    private static HostedServiceProbe First(string types, string statement) => new("Holder", types, statement, "_ = 0;");
}
