using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of the forms of a C# 14 extension block member (issue 511) beyond the two of <see cref="TwistProbeTests"/>. The
/// compiler emits each member of a block <c>extension(R r) { … }</c> as a static method of the enclosing class whose parameters are the
/// receiver, for an instance member, then the member's own, and a call of the member, <c>x.M(a)</c>, an access of its property or a use
/// of its operator, as a call of that method, <c>E.M(x, a)</c>, the receiver passed as its parameter's kind says (the C# 14 extension
/// members feature specification; Roslyn's <c>IMethodSymbol.AssociatedExtensionImplementation</c>). So every probe has a counterpart
/// written without a block — the static or classic <c>this</c> method the compiler emits, under the same names — and run time calls for
/// the same findings in both: each probe asserts its truth on the block form and on the counterpart. Two hosted services share one
/// singleton <c>Holder</c> through the whole analysis (<see cref="HostedServiceProbe"/>).</summary>
public sealed class ExtensionBlockProbeTests
{
    /// <summary>A struct with one value, the receiver of the struct forms.</summary>
    private const string COUNTER = "public struct Counter { public int Value; }\n";

    /// <summary>A struct holding a box, the receiver a member taking it by <c>ref</c> replaces whole.</summary>
    private const string WRAPPER = "public struct Wrapper { public Box Target; }\n";

    /// <summary>The lost update of <c>Value</c> in two runs of <paramref name="member"/>.</summary>
    /// <param name="member">The member that increments it.</param>
    private static string ValueIncrementedIn(string member) => $"DCA1002 unprotected Value: ReadModifyWrite {member} | ReadModifyWrite {member}";

    /// <summary>Each probe by name: the block form, its counterpart without a block, the statements the two hosted services run on
    /// <c>Holder</c>, and the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them.</summary>
    private static readonly Dictionary<string, (string Block, string Counterpart, string First, string Second, string[] Truth)> Probes =
        new(StringComparer.Ordinal)
        {
            ["static-form-call"] = (
                "public static class BoxExtensions { extension(Box box) { public void Bump() => box.Value++; } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => BoxExtensions.Bump(_box); }",
                "public static class BoxExtensions { public static void Bump(Box box) => box.Value++; }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => BoxExtensions.Bump(_box); }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("BoxExtensions.Bump")]),
            ["property-getter-against-setter"] = (
                "public static class BoxExtensions { extension(Box box) { public int Doubled { get => box.Value * 2; set => box.Value = value / 2; } } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public int Read() => _box.Doubled; public void Write() => _box.Doubled = 4; }",
                "public static class BoxExtensions { public static int get_Doubled(Box box) => box.Value * 2; public static void set_Doubled(Box box, int value) => box.Value = value / 2; }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public int Read() => BoxExtensions.get_Doubled(_box); public void Write() => BoxExtensions.set_Doubled(_box, 4); }",
                "_ = _holder.Read();", "_holder.Write();", ["DCA1001 unprotected Value: Read BoxExtensions.get_Doubled | Write BoxExtensions.set_Doubled"]),
            ["property-compound-assignment"] = (
                "public static class BoxExtensions { extension(Box box) { public int Doubled { get => box.Value * 2; set => box.Value = value / 2; } } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Doubled += 2; }",
                "public static class BoxExtensions { public static int get_Doubled(Box box) => box.Value * 2; public static void set_Doubled(Box box, int value) => box.Value = value / 2; }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => BoxExtensions.set_Doubled(_box, BoxExtensions.get_Doubled(_box) + 2); }",
                "_holder.Run();", "_holder.Run();", ["DCA1002 unprotected Value: ReadModifyWrite BoxExtensions.set_Doubled | ReadModifyWrite BoxExtensions.set_Doubled"]),
            ["static-form-accessor-call"] = (
                "public static class BoxExtensions { extension(Box box) { public int Doubled { get => box.Value * 2; set => box.Value = value / 2; } } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => BoxExtensions.set_Doubled(_box, 4); }",
                "public static class BoxExtensions { public static int get_Doubled(Box box) => box.Value * 2; public static void set_Doubled(Box box, int value) => box.Value = value / 2; }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => BoxExtensions.set_Doubled(_box, 4); }",
                "_holder.Run();", "_holder.Run();", ["DCA1001 unprotected Value: Write BoxExtensions.set_Doubled | Write BoxExtensions.set_Doubled"]),
            ["static-member-of-a-block"] = (
                "public static class BoxExtensions { extension(Box) { public static void Bump(Box target) => target.Value++; } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => Box.Bump(_box); }",
                "public static class BoxExtensions { public static void Bump(Box target) => target.Value++; }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => BoxExtensions.Bump(_box); }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("BoxExtensions.Bump")]),
            ["struct-receiver-by-value"] = (
                COUNTER + "public static class BoxExtensions { extension(Counter counter) { public void Bump() => counter.Value++; } }\n" +
                "public sealed class Holder { private Counter _counter; public void Run() => _counter.Bump(); }",
                COUNTER + "public static class BoxExtensions { public static void Bump(this Counter counter) => counter.Value++; }\n" +
                "public sealed class Holder { private Counter _counter; public void Run() => _counter.Bump(); }",
                "_holder.Run();", "_holder.Run();", []),
            ["struct-receiver-by-value-against-a-write"] = (
                COUNTER + "public static class Counters { public static int Last; }\n" +
                "public static class BoxExtensions { extension(Counter counter) { public void Keep() => Counters.Last = counter.Value; } }\n" +
                "public sealed class Holder { private Counter _counter; public void Run() => _counter.Keep(); public void Write() => _counter.Value = 1; }",
                COUNTER + "public static class Counters { public static int Last; }\n" +
                "public static class BoxExtensions { public static void Keep(this Counter counter) => Counters.Last = counter.Value; }\n" +
                "public sealed class Holder { private Counter _counter; public void Run() => _counter.Keep(); public void Write() => _counter.Value = 1; }",
                "_holder.Run();", "_holder.Write();", ["DCA1001 unprotected _counter.Value: Read Holder.Run | Write Holder.Write"]),
            ["struct-receiver-by-ref"] = (
                COUNTER + "public static class BoxExtensions { extension(ref Counter counter) { public void Bump() => counter.Value++; } }\n" +
                "public sealed class Holder { private Counter _counter; public void Run() => _counter.Bump(); }",
                COUNTER + "public static class BoxExtensions { public static void Bump(this ref Counter counter) => counter.Value++; }\n" +
                "public sealed class Holder { private Counter _counter; public void Run() => _counter.Bump(); }",
                "_holder.Run();", "_holder.Run();", ["DCA1002 unprotected _counter.Value: ReadModifyWrite BoxExtensions.Bump | ReadModifyWrite BoxExtensions.Bump"]),
            ["local-by-ref-receiver-the-member-replaces"] = (
                WRAPPER + "public static class BoxExtensions { extension(ref Wrapper wrapper) { public void Rebind(Box replacement) => wrapper = new Wrapper { Target = replacement }; } }\n" +
                "public sealed class Holder { public readonly Box Old = new(); public void Run() { var wrapper = new Wrapper { Target = Old }; wrapper.Rebind(new Box()); wrapper.Target.Value++; } public void Write() => Old.Value = 1; }",
                WRAPPER + "public static class BoxExtensions { public static void Rebind(this ref Wrapper wrapper, Box replacement) => wrapper = new Wrapper { Target = replacement }; }\n" +
                "public sealed class Holder { public readonly Box Old = new(); public void Run() { var wrapper = new Wrapper { Target = Old }; wrapper.Rebind(new Box()); wrapper.Target.Value++; } public void Write() => Old.Value = 1; }",
                "_holder.Run();", "_holder.Write();", []),
            ["local-by-ref-receiver-the-setter-replaces"] = (
                WRAPPER + "public static class BoxExtensions { extension(ref Wrapper wrapper) { public Box Current { get => wrapper.Target; set => wrapper = new Wrapper { Target = value }; } } }\n" +
                "public sealed class Holder { public readonly Box Old = new(); public void Run() { var wrapper = new Wrapper { Target = Old }; wrapper.Current = new Box(); wrapper.Target.Value++; } public void Write() => Old.Value = 1; }",
                WRAPPER + "public static class BoxExtensions { public static Box get_Current(this ref Wrapper wrapper) => wrapper.Target; public static void set_Current(this ref Wrapper wrapper, Box value) => wrapper = new Wrapper { Target = value }; }\n" +
                "public sealed class Holder { public readonly Box Old = new(); public void Run() { var wrapper = new Wrapper { Target = Old }; BoxExtensions.set_Current(ref wrapper, new Box()); wrapper.Target.Value++; } public void Write() => Old.Value = 1; }",
                "_holder.Run();", "_holder.Write();", []),
            ["struct-receiver-by-in"] = (
                COUNTER + "public static class BoxExtensions { extension(in Counter counter) { public int Peek() => counter.Value; } }\n" +
                "public sealed class Holder { private Counter _counter; public int Read() => _counter.Peek(); public void Write() => _counter.Value = 1; }",
                COUNTER + "public static class BoxExtensions { public static int Peek(this in Counter counter) => counter.Value; }\n" +
                "public sealed class Holder { private Counter _counter; public int Read() => _counter.Peek(); public void Write() => _counter.Value = 1; }",
                "_ = _holder.Read();", "_holder.Write();", ["DCA1001 unprotected _counter.Value: Read BoxExtensions.Peek | Write Holder.Write"]),
            ["generic-block-with-a-constraint"] = (
                "public sealed class Bag<T> { public T? Item; }\n" +
                "public static class BoxExtensions { extension<T>(Bag<T> bag) where T : class { public void Put(T item) => bag.Item = item; } }\n" +
                "public sealed class Holder { private readonly Bag<string> _bag = new(); public void Run() => _bag.Put(\"x\"); }",
                "public sealed class Bag<T> { public T? Item; }\n" +
                "public static class BoxExtensions { public static void Put<T>(this Bag<T> bag, T item) where T : class => bag.Item = item; }\n" +
                "public sealed class Holder { private readonly Bag<string> _bag = new(); public void Run() => _bag.Put(\"x\"); }",
                "_holder.Run();", "_holder.Run();", ["DCA1001 unprotected Item: Write BoxExtensions.Put<T> | Write BoxExtensions.Put<T>"]),
            ["generic-receiver-dispatched-through-its-constraint"] = (
                "public interface ITicker { void Increment(); }\npublic sealed class Ticker : ITicker { public int Count; public void Increment() => Count++; }\n" +
                "public static class BoxExtensions { extension<T>(T ticker) where T : ITicker { public void Bump() => ticker.Increment(); } }\n" +
                "public sealed class Holder { private readonly Ticker _ticker = new(); public void Run() => _ticker.Bump(); }",
                "public interface ITicker { void Increment(); }\npublic sealed class Ticker : ITicker { public int Count; public void Increment() => Count++; }\n" +
                "public static class BoxExtensions { public static void Bump<T>(this T ticker) where T : ITicker => ticker.Increment(); }\n" +
                "public sealed class Holder { private readonly Ticker _ticker = new(); public void Run() => _ticker.Bump(); }",
                "_holder.Run();", "_holder.Run();", ["DCA1002 unprotected Count: ReadModifyWrite Ticker.Increment | ReadModifyWrite Ticker.Increment"]),
            ["nullable-receiver"] = (
                "public static class BoxExtensions { extension(Box? box) { public void Bump() { if (box is not null) box.Value++; } } }\n" +
                "public sealed class Holder { private readonly Box? _box = new(); public void Run() => _box.Bump(); }",
                "public static class BoxExtensions { public static void Bump(this Box? box) { if (box is not null) box.Value++; } }\n" +
                "public sealed class Holder { private readonly Box? _box = new(); public void Run() => _box.Bump(); }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("BoxExtensions.Bump")]),
            ["operator-of-a-block"] = (
                "public static class BoxExtensions { extension(Box) { public static Box operator +(Box box, int amount) { box.Value += amount; return box; } } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _ = _box + 1; }",
                "public static class BoxExtensions { public static Box op_Addition(Box box, int amount) { box.Value += amount; return box; } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _ = BoxExtensions.op_Addition(_box, 1); }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("BoxExtensions.op_Addition")]),
            ["lambda-capturing-the-receiver-invoked-inline"] = (
                "public static class BoxExtensions { extension(Box box) { public void Bump() { Action bump = () => box.Value++; bump(); } } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "public static class BoxExtensions { public static void Bump(this Box box) { Action bump = () => box.Value++; bump(); } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("BoxExtensions.Bump")]),
            ["task-capturing-the-receiver"] = (
                "public static class BoxExtensions { extension(Box box) { public void Bump() => Task.Run(() => box.Value++); } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "public static class BoxExtensions { public static void Bump(this Box box) => Task.Run(() => box.Value++); }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("BoxExtensions.Bump")]),
            ["local-function-capturing-the-receiver"] = (
                "public static class BoxExtensions { extension(Box box) { public void Bump() { Increment(); void Increment() => box.Value++; } } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "public static class BoxExtensions { public static void Bump(this Box box) { Increment(); void Increment() => box.Value++; } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("BoxExtensions.Bump")]),
            ["receiver-passed-on-to-a-call"] = (
                "public static class Boxes { public static void Increment(Box target) => target.Value++; }\n" +
                "public static class BoxExtensions { extension(Box box) { public void Bump() => Boxes.Increment(box); } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "public static class Boxes { public static void Increment(Box target) => target.Value++; }\n" +
                "public static class BoxExtensions { public static void Bump(this Box box) => Boxes.Increment(box); }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("Boxes.Increment")]),
            ["receiver-held-in-a-local"] = (
                "public static class BoxExtensions { extension(Box box) { public void Bump() { var target = box; target.Value++; } } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "public static class BoxExtensions { public static void Bump(this Box box) { var target = box; target.Value++; } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("BoxExtensions.Bump")]),
            ["parameter-of-a-member"] = (
                "public static class BoxExtensions { extension(Box box) { public void Add(Box other) => other.Value += box.Value; } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); private readonly Box _total = new(); public void Run() => _box.Add(_total); }",
                "public static class BoxExtensions { public static void Add(this Box box, Box other) => other.Value += box.Value; }\n" +
                "public sealed class Holder { private readonly Box _box = new(); private readonly Box _total = new(); public void Run() => _box.Add(_total); }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("BoxExtensions.Add")]),
            ["deconstruct-of-a-block"] = (
                "public static class BoxExtensions { extension(Box box) { public void Deconstruct(out int value, out int next) { value = box.Value++; next = value + 1; } } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public int Run() { var (value, next) = _box; return value + next; } }",
                "public static class BoxExtensions { public static void Deconstruct(this Box box, out int value, out int next) { value = box.Value++; next = value + 1; } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public int Run() { var (value, next) = _box; return value + next; } }",
                "_ = _holder.Run();", "_ = _holder.Run();", [ValueIncrementedIn("BoxExtensions.Deconstruct")]),
            ["method-group-of-a-block-member"] = (
                "public static class BoxExtensions { extension(Box box) { public void Bump() => box.Value++; } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() { Action bump = _box.Bump; bump(); } }",
                "public static class BoxExtensions { public static void Bump(this Box box) => box.Value++; }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() { Action bump = _box.Bump; bump(); } }",
                "_holder.Run();", "_holder.Run();", [ValueIncrementedIn("BoxExtensions.Bump")]),
            ["async-member"] = (
                "public static class BoxExtensions { extension(Box box) { public async Task BumpAsync() { await Task.Yield(); box.Value++; } } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public Task RunAsync() => _box.BumpAsync(); }",
                "public static class BoxExtensions { public static async Task BumpAsync(this Box box) { await Task.Yield(); box.Value++; } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public Task RunAsync() => _box.BumpAsync(); }",
                "_ = _holder.RunAsync();", "_ = _holder.RunAsync();", [ValueIncrementedIn("BoxExtensions.BumpAsync")]),
            ["member-under-a-lock"] = (
                "public static class BoxExtensions { extension(Box box) { public void Bump() { lock (box) box.Value++; } } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "public static class BoxExtensions { public static void Bump(this Box box) { lock (box) box.Value++; } }\n" +
                "public sealed class Holder { private readonly Box _box = new(); public void Run() => _box.Bump(); }",
                "_holder.Run();", "_holder.Run();", [])
        };

    /// <summary>Why a delegate of an extension method group loses the method's accesses, in both forms.</summary>
    private const string METHOD_GROUP_DROPS_ITS_RECEIVER =
        "issue 548: a delegate made from an extension method group, classic or of a block, drops the receiver it is bound to as its " +
        "first argument, so the method's accesses are lost";

    /// <summary>Why a local an extension block member takes by <c>ref</c> keeps, after the call, the value the member replaced.</summary>
    private const string REF_RECEIVER_IS_NOT_WRITTEN_BACK =
        "issue 553: a receiver an extension block member takes by ref is no ref argument of the implementation for the SSA plan and the " +
        "lowering, so the local the member is called on gets no new version, and what is read through it after the call is the struct " +
        "the member replaced";

    /// <summary>The probes whose block form the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["method-group-of-a-block-member"] = new([], METHOD_GROUP_DROPS_ITS_RECEIVER),
        ["local-by-ref-receiver-the-member-replaces"] = new(["DCA1002 unprotected Value: ReadModifyWrite Holder.Run | Write Holder.Write"],
                                                            REF_RECEIVER_IS_NOT_WRITTEN_BACK),
        ["local-by-ref-receiver-the-setter-replaces"] = new(["DCA1002 unprotected Value: ReadModifyWrite Holder.Run | Write Holder.Write"],
                                                            REF_RECEIVER_IS_NOT_WRITTEN_BACK)
    };

    /// <summary>The probes whose counterpart without a block the analysis answers otherwise than run time calls for.</summary>
    private static readonly Dictionary<string, ProbeDifference> CounterpartDifferences = new(StringComparer.Ordinal)
    {
        ["method-group-of-a-block-member"] = new([], METHOD_GROUP_DROPS_ITS_RECEIVER)
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The block form's findings are what run time calls for, or the answer its difference pins, read as sets.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Block_form_answers_as_run_time_calls_for_or_as_its_named_difference(string name)
    {
        var (block, _, first, second, truth) = Probes[name];

        var findings = await new HostedServiceProbe("Holder", block, first, second).FindingsAsync();

        HostedServiceProbe.AssertAnswer(findings, truth, Differences.GetValueOrDefault(name));
    }

    /// <summary>The counterpart the compiler emits for the block form answers as run time calls for, or as its own difference pins.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Counterpart_without_a_block_answers_as_run_time_calls_for_or_as_its_named_difference(string name)
    {
        var (_, counterpart, first, second, truth) = Probes[name];

        var findings = await new HostedServiceProbe("Holder", counterpart, first, second).FindingsAsync();

        HostedServiceProbe.AssertAnswer(findings, truth, CounterpartDifferences.GetValueOrDefault(name));
    }

    /// <summary>A difference of either form names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth()
    {
        var truths = Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal);

        HostedServiceProbe.AssertDifferences(truths, Differences);
        HostedServiceProbe.AssertDifferences(truths, CounterpartDifferences);
    }

    /// <summary>The implementation of a block member reads its receiver as its own first parameter, and a lambda of the member that names
    /// the receiver captures it: the lambda reads the receiver's capture cell, the member's closure named after the implementation.</summary>
    [Fact]
    public async Task A_lambda_of_a_block_member_captures_the_receiver_the_member_takes_as_its_parameter()
    {
        const string source = """
            public sealed class Box { public int Value; }
            public static class BoxExtensions { extension(Box box) { public void Bump() { Action bump = () => box.Value++; bump(); } } }
            """;
        var compilation = (await FixtureSolution.Create(("Case.cs", Usings + source)).Projects.Single().GetCompilationAsync())!;
        var implementation = compilation.GetTypeByMetadataName("BoxExtensions")!.GetMembers("Bump").OfType<IMethodSymbol>().Single();

        var lowered = IrLowering.Lower(implementation, compilation, ROOT_DIRECTORY, CancellationToken.None);

        var receiver = Assert.Single(lowered.Body.Parameters);
        Assert.Equal(("box", 0), (receiver.Name, receiver.Ordinal));
        Assert.DoesNotContain(lowered.Body.Blocks.SelectMany(block => block.Operations), operation => operation is IrCaptureOperation);
        var lambda = Assert.Single(lowered.NestedBodies);
        var capture = Assert.Single(lambda.Blocks.SelectMany(block => block.Operations).OfType<IrCaptureOperation>());
        Assert.Equal(("closure:BoxExtensions.Bump(Box)", "box"), (capture.Cell!.Region, capture.Cell.Slot));
    }
}
