using System.Collections.Concurrent;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What an operation reaches in the reachable set of CoreLib's generation and what the heap makes of it (ADR 0019, R1–R3):
/// every operation that reaches a body, crossed with how an object of the callee's declaring type comes to exist, where that making
/// stands, and the dispatch rule. Each row runs one small program through <see cref="ScopePipeline.Run"/>: the hosted service
/// <c>Worker</c> calls <c>Operate</c>, which runs the operation, and then <c>Make</c>, which makes the object of an <c>after</c> row.
/// Each channel of an operation has an object of its own — the argument an <c>Arg</c>, the <c>out</c> argument a field of a
/// <c>Sink</c>, the handed lambda a write to a <c>Seen</c> — so an unknown effect through one channel never passes for another's.</summary>
public sealed class CoreLibGenerationSetMatrixTests
{
    /// <summary>The type the constructed-types rows leave out, as <c>System.SR</c> is left out of CoreLib's generation.</summary>
    private const string LEFT_OUT = "LeftOut";

    /// <summary>The member that runs every operation.</summary>
    private const string OPERATE = "Worker.Operate()";

    /// <summary>The operation that reaches a body.</summary>
    public enum Operation
    {
        /// <summary>A static method call.</summary>
        Static,

        /// <summary>A read of a static field, which activates the type's initializer.</summary>
        StaticField,

        /// <summary>An instance property's get and set: its accessors.</summary>
        Property,

        /// <summary>A non-virtual call of an instance method.</summary>
        Instance,

        /// <summary>A constructor call.</summary>
        Constructor,

        /// <summary>A virtual call.</summary>
        Virtual,

        /// <summary>An interface call.</summary>
        Interface,

        /// <summary>A delegate created on a static method, then invoked.</summary>
        DelegateStatic,

        /// <summary>A delegate created on a non-virtual instance method, then invoked.</summary>
        DelegateInstance,

        /// <summary>A delegate created on a virtual method, then invoked.</summary>
        DelegateVirtual,

        /// <summary>A delegate created on an interface method, then invoked.</summary>
        DelegateInterface,

        /// <summary>A delegate on a virtual method handed to <c>Task.Run</c> as its work.</summary>
        SpawnDelegate,

        /// <summary>An object handed to <c>ThreadPool.UnsafeQueueUserWorkItem</c>, whose <c>IThreadPoolWorkItem.Execute</c> the spawn
        /// calls.</summary>
        SpawnMethod
    }

    /// <summary>How an object of the callee's declaring type comes to exist.</summary>
    public enum Making
    {
        /// <summary>A constructor call of that type.</summary>
        Constructed,

        /// <summary>A constructor call of a type derived from it, inheriting the callee.</summary>
        DerivedConstructed,

        /// <summary>A construction the reachable set starts: a DI service the root takes.</summary>
        RootConstructed,

        /// <summary>The type is a value type and is boxed.</summary>
        Boxed,

        /// <summary>The type is a value type constructed with <c>new</c> and never boxed; the callee is reached through a constrained
        /// call.</summary>
        Unboxed,

        /// <summary>No object of the type is made.</summary>
        NeverConstructed,

        /// <summary>The declaring type is in the left-out set; an object of it the operation needs is constructed with <c>new</c>, and the
        /// left-out member holds a lambda of its own.</summary>
        LeftOut,

        /// <summary>The receiver of a dispatching operation is an object of a left-out type or an object of a constructed type in the
        /// set.</summary>
        LeftOutMixed
    }

    /// <summary>Where the making stands.</summary>
    public enum Timing
    {
        /// <summary>In the operation's own body before it, or in a body reached earlier.</summary>
        Before,

        /// <summary>Only in a body reached after the operation's body, so the dispatch is met first and its target waits.</summary>
        After
    }

    /// <summary>The dispatch rule of the run.</summary>
    public enum Mode
    {
        /// <summary>Class-hierarchy dispatch, with no type left out.</summary>
        ClassHierarchy,

        /// <summary>Constructed-types dispatch, with <see cref="LEFT_OUT"/> left out.</summary>
        ConstructedTypes
    }

    /// <summary>A channel through which an operation's callee touches what the operation hands it.</summary>
    [Flags]
    private enum Channel
    {
        None = 0,
        Argument = 1,
        Receiver = 2,
        Out = 4,
        Lambda = 8
    }

    /// <summary>A cell of the matrix.</summary>
    /// <param name="Operation">The operation.</param>
    /// <param name="Making">How an object of the callee's type comes to exist.</param>
    /// <param name="Timing">Where that making stands.</param>
    /// <param name="Mode">The dispatch rule.</param>
    private sealed record Cell(Operation Operation, Making Making, Timing Timing, Mode Mode)
    {
        public override string ToString() => $"{Operation}/{Making}/{Timing}/{Mode}";
    }

    /// <summary>What a cell is expected to give.</summary>
    /// <param name="Reached">Whether every body of the callee is in the reachable set.</param>
    /// <param name="Bound">Whether the heap binds the operation to every body of the callee — for a static field, activates the type
    /// initializer — and the callee touches each channel itself.</param>
    /// <param name="OtherBound">Whether the heap binds the operation to the implementation of the other receiver: <c>Other</c>'s where
    /// the callee's type is never constructed, <c>Impl</c>'s in a mixed row.</param>
    /// <param name="Opaque">Whether the operation's resolution into the callee is an opaque call, which touches each of its channels from
    /// where the operation stands.</param>
    /// <param name="Unseen">Whether the operation is a spawn whose work resolves into a member without a body: no body of the type runs,
    /// the work's object gets no effect, and a task the spawn returns completes unseen.</param>
    /// <param name="Reason">The requirements the expectation follows.</param>
    private sealed record Expectation(bool Reached, bool Bound, bool OtherBound, bool Opaque, bool Unseen, string Reason);

    /// <summary>The cells whose outcome is known to be wider than the requirements ask: none.</summary>
    private static readonly Cell[] KnownWider = [];

    /// <summary>The cells with a known gap: a spawn's work resolved into a member without a body makes its task complete unseen and has no
    /// other effect (R3's exception, the gap R8 records).</summary>
    private static readonly Cell[] KnownGap =
        (from operation in new[] { Operation.SpawnDelegate, Operation.SpawnMethod }
         from making in new[] { Making.LeftOut, Making.LeftOutMixed }
         select new Cell(operation, making, Timing.Before, Mode.ConstructedTypes)).ToArray();

    /// <summary>The programs of the rows, by source: a class-hierarchy row and its constructed-types twin share one compilation.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Solution>> Programs = new(StringComparer.Ordinal);

    // ---- the cells ----

    private static IEnumerable<Cell> Cells() =>
        from operation in Enum.GetValues<Operation>()
        from making in Enum.GetValues<Making>()
        from timing in Enum.GetValues<Timing>()
        from mode in Enum.GetValues<Mode>()
        let cell = new Cell(operation, making, timing, mode)
        where Allowed(cell)
        select cell;

    public static TheoryData<Operation, Making, Timing, Mode> Rows()
    {
        var rows = new TheoryData<Operation, Making, Timing, Mode>();
        foreach (var cell in Cells())
            rows.Add(cell.Operation, cell.Making, cell.Timing, cell.Mode);
        return rows;
    }

    /// <summary>Whether a combination of the axes is a row.</summary>
    /// <param name="cell">The combination.</param>
    private static bool Allowed(Cell cell)
    {
        var (operation, making) = (cell.Operation, cell.Making);
        // No construction concerns a static member: the plain call, and its left-out twin.
        if (operation is Operation.Static or Operation.StaticField or Operation.DelegateStatic &&
            making is not (Making.NeverConstructed or Making.LeftOut))
            return false;
        // A non-virtual member needs an object, is not narrowed, and has one callee whatever the receiver, so no mixed receiver.
        if (operation is Operation.Property or Operation.Instance or Operation.DelegateInstance &&
            making is not (Making.Constructed or Making.DerivedConstructed or Making.LeftOut))
            return false;
        // The operation is itself the construction.
        if (operation == Operation.Constructor && making is not (Making.NeverConstructed or Making.LeftOut))
            return false;
        // A delegate on a value's member boxes it, and a spawn takes an object.
        if (making == Making.Unboxed && (IsDelegate(operation) || IsSpawn(operation)))
            return false;
        // Nothing waits to be timed but a dispatch whose type a body constructs or boxes.
        if (cell.Timing == Timing.After &&
            (making is not (Making.Constructed or Making.DerivedConstructed or Making.Boxed) || !IsDispatching(operation)))
            return false;
        // A class-hierarchy run has no left-out set.
        return cell.Mode != Mode.ClassHierarchy || making is not (Making.LeftOut or Making.LeftOutMixed);
    }

    private static bool IsDispatching(Operation operation) =>
        operation is Operation.Virtual or Operation.Interface or Operation.DelegateVirtual or Operation.DelegateInterface or
            Operation.SpawnDelegate or Operation.SpawnMethod;

    private static bool IsDelegate(Operation operation) =>
        operation is Operation.DelegateStatic or Operation.DelegateInstance or Operation.DelegateVirtual or Operation.DelegateInterface;

    private static bool IsSpawn(Operation operation) => operation is Operation.SpawnDelegate or Operation.SpawnMethod;

    private static bool IsValueType(Making making) => making is Making.Boxed or Making.Unboxed;

    // ---- the table ----

    /// <summary>What a cell is expected to give, by the values of its axes.</summary>
    /// <param name="cell">The cell.</param>
    private static Expectation Expected(Cell cell)
    {
        var (operation, making) = (cell.Operation, cell.Making);
        if (cell.Mode == Mode.ClassHierarchy)
        {
            // R7: class-hierarchy dispatch keeps today's outcome. Every override is reached; where no object of its type is, nothing binds
            // it and the constructed type's implementation runs instead.
            if (making == Making.NeverConstructed && IsDispatching(operation))
                return new(Reached: true, Bound: false, OtherBound: true, Opaque: false, Unseen: false, "R7");
            // R7: the heap binds the callee wherever an object of its type is the receiver — for an unboxed value, the region its `new`
            // allocates, which the constrained call resolves by type.
            return new(Reached: true, Bound: true, OtherBound: false, Opaque: false, Unseen: false, "R7");
        }

        switch (making)
        {
            // R2, R3's exception: no body of a left-out type is reached, and a spawn's work resolved into it, a work without a body, makes
            // its task complete unseen and has no other effect.
            case Making.LeftOut when IsSpawn(operation):
                return new(Reached: false, Bound: false, OtherBound: false, Opaque: false, Unseen: true, "R2, R3 (exception)");
            // R2, R3: the left-out type's initializer is neither reached nor activated.
            case Making.LeftOut when operation == Operation.StaticField:
                return new(Reached: false, Bound: false, OtherBound: false, Opaque: false, Unseen: false, "R2, R3");
            // R2, R3: a call resolved into a member without a body is an opaque call.
            case Making.LeftOut:
                return new(Reached: false, Bound: false, OtherBound: false, Opaque: true, Unseen: false, "R2, R3");
            // R1, R3's exception: the in-set work runs, and the left-out one is a work without a body.
            case Making.LeftOutMixed when IsSpawn(operation):
                return new(Reached: false, Bound: false, OtherBound: true, Opaque: false, Unseen: true, "R1, R3 (exception)");
            // R1, R3: the in-set receiver's implementation is reached and bound, and the left-out receiver's resolution is opaque.
            case Making.LeftOutMixed:
                return new(Reached: false, Bound: false, OtherBound: true, Opaque: true, Unseen: false, "R1, R3");
        }

        // R1 narrows only dispatch: a static member, an accessor, a non-virtual member and a constructor are reached and bound as today,
        // and a static field activates its initializer.
        if (!IsDispatching(operation))
            return new(Reached: true, Bound: true, OtherBound: false, Opaque: false, Unseen: false, "R1");
        return making switch
        {
            // R1: the type is constructed, by a body before or after the dispatch or by the set itself, or a value of it is boxed; a target
            // reached late is bound to the operation already met.
            Making.Constructed or Making.DerivedConstructed or Making.RootConstructed or Making.Boxed =>
                new(Reached: true, Bound: true, OtherBound: false, Opaque: false, Unseen: false, "R1"),
            // R1: nothing the set counts as a construction makes the type, so only the constructed types' implementations bind.
            Making.NeverConstructed => new(Reached: false, Bound: false, OtherBound: true, Opaque: false, Unseen: false, "R1"),
            // R1, R3: a value type counts only once boxed, but the heap holds the region its `new` allocates, so the call's resolution on
            // it is opaque — never nothing.
            Making.Unboxed => new(Reached: false, Bound: false, OtherBound: false, Opaque: true, Unseen: false, "R1, R3"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
    }

    /// <summary>The channels an operation's form carries: what it passes the callee, and its receiver.</summary>
    /// <param name="cell">The cell.</param>
    private static Channel Carried(Cell cell) => cell.Operation switch
    {
        Operation.Static or Operation.Constructor or Operation.DelegateStatic => Channel.Argument | Channel.Out | Channel.Lambda,
        Operation.StaticField => Channel.None,
        // The setter is handed an Arg; neither accessor takes an `out` argument or a lambda.
        Operation.Property => Channel.Argument | Channel.Receiver,
        // A value type's virtual callee is an override of `object.Equals(object)`, which takes one argument.
        Operation.Virtual or Operation.DelegateVirtual when IsValueType(cell.Making) => Channel.Argument | Channel.Receiver,
        Operation.Instance or Operation.Virtual or Operation.Interface or Operation.DelegateInstance or Operation.DelegateVirtual or
            Operation.DelegateInterface => Channel.Argument | Channel.Receiver | Channel.Out | Channel.Lambda,
        Operation.SpawnDelegate or Operation.SpawnMethod => Channel.Receiver,
        _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
    };

    /// <summary>The channels an opaque resolution is checked on, each from where the operation stands (R3): the argument and the
    /// receiver as the form carries them, never a constructor's new object; the <c>out</c> argument where the invocation or object
    /// creation names the left-out member; and the handed lambda in an unknown execution where no in-set implementation runs it. An
    /// unboxed row checks the receiver and the argument alone.</summary>
    /// <param name="cell">The cell.</param>
    private static Channel OpaqueChannels(Cell cell)
    {
        var carried = Carried(cell) & ~(cell.Operation == Operation.Constructor ? Channel.Receiver : Channel.None);
        if (cell.Making == Making.Unboxed)
            return carried & (Channel.Argument | Channel.Receiver);
        if (cell.Making == Making.LeftOutMixed || !NamesTheCallee(cell.Operation))
            carried &= ~Channel.Out;
        if (cell.Making == Making.LeftOutMixed)
            carried &= ~Channel.Lambda;
        return carried;
    }

    /// <summary>Whether the operation's invocation or object creation names the callee itself, so the lowering writes the <c>out</c>
    /// argument of a callee without a body where the operation stands: not a dispatch, which names the in-set declaration, and not a
    /// delegate's <c>Invoke</c>.</summary>
    /// <param name="operation">The operation.</param>
    private static bool NamesTheCallee(Operation operation) => operation is Operation.Static or Operation.Instance or Operation.Constructor;

    /// <summary>Whether an object of the callee's declaring type is a receiver of the operation.</summary>
    /// <param name="cell">The cell.</param>
    private static bool HasReceiverObject(Cell cell) =>
        cell.Making != Making.NeverConstructed &&
        cell.Operation is not (Operation.Static or Operation.StaticField or Operation.DelegateStatic or Operation.Constructor);

    [Theory]
    [InlineData("Box(1).ToString(); Ignore(new S());", "", false)]
    [InlineData("Outer(new S()).ToString();", "static object Outer<T>(T x) => Box(x);", true)]
    [InlineData("new Holder<S>().Get(default).ToString();", "", true)]
    [InlineData("GC.KeepAlive(unused); new Holder<int>().Get(1).ToString();", "static Holder<S> unused;", false)]
    [InlineData("Holder<S> unused = null!; GC.KeepAlive(unused); new Holder<int>().Get(1).ToString();", "", false)]
    [InlineData("Observe(null!); new Holder<int>().Get(1).ToString();", "static void Observe(Holder<S> unused) => GC.KeepAlive(unused);", false)]
    [InlineData("IHolder<S> holder = new Holder<S>(); holder.Get(default).ToString();", "", true)]
    [InlineData("Box(1); Later();", "static void Later() => Outer(new S()).ToString(); static object Outer<T>(T x) => Box(x);", true)]
    [InlineData("Func<S, object> box = Box<S>; box(default).ToString();", "", true)]
    [InlineData("new Container<S>.Nested<int>().Get(default).ToString();", "", true)]
    [InlineData("new Container<int>.Nested<S>().Get(default).ToString();", "", false)]
    [InlineData("new Holder<S>().Shadow(1).ToString();", "", false)]
    [InlineData("Box((S?)new S()).ToString();", "", true)]
    [InlineData("Box(1).ToString(); Other(new S());", "static void Other<T>(T x) { }", false)]
    [InlineData("Select(1, new S()).ToString();", "static object Select<T, U>(T x, U y) => x!;", false)]
    [InlineData("Select(new S(), 1).ToString();", "static object Select<T, U>(T x, U y) => x!;", true)]
    public async Task Boxed_parameter_only_admits_its_bindings(string operation, string helper, bool reached)
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + $$"""
            public class LeftOut { }
            public struct S { public override string ToString() => "S"; }
            public struct Unboxed { public override string ToString() => "unboxed"; }
            public interface IHolder<T> { object Get(T x); }
            public class Holder<T> : IHolder<T> { public object Get(T x) => x!; public object Shadow<T>(T x) => x!; }
            public class Container<T> { public class Nested<U> { public object Get(T x) => x!; } }
            public class Worker : BackgroundService
            {
                static object Box<T>(T x) => x!;
                static void Ignore<U>(U x) { }
                {{helper}}
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    {{operation}}
                    Ignore(new Unboxed());
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddHostedService<Worker>();")));
        foreach (var mode in new[] { Mode.ConstructedTypes, Mode.ClassHierarchy })
        {
            var run = await Run(solution, mode);
            Assert.False(run.Stopped);
            Assert.Equal(mode == Mode.ClassHierarchy || reached, run.Reachable.ReachedBodies.ContainsKey("body:Fixture:M:S.ToString"));
            Assert.Equal(mode == Mode.ClassHierarchy, run.Reachable.ReachedBodies.ContainsKey("body:Fixture:M:Unboxed.ToString"));
        }
    }

    [Theory]
    [InlineData("Box(new S()).ToString();", "static object Box<T>(T value) where T : struct => value;")]
    [InlineData("Box(new S()).ToString();", "static object Box<T>(T value) where T : struct => Forward(value); static object Forward<U>(U value) where U : struct => value;")]
    [InlineData("Box<S>.Run(new S()).ToString();", "")]
    [InlineData("Bind(new S())();", "static Func<string> Bind<T>(T value) where T : struct => value.ToString;")]
    [InlineData("Box((S?)new S()).ToString();", "static object Box<T>(T? value) where T : struct => value;")]
    [InlineData("((object)(S?)new S()).ToString();", "")]
    [InlineData("Box<S>.Bind(new S())();", "")]
    [InlineData("Bind((S?)new S())(); Observe(null!);", "static Func<string> Bind<T>(T? value) where T : struct => value.ToString; static void Observe(object value) => value.ToString();")]
    public async Task Generic_boxing_reaches_value_overrides(string operation, string helper)
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + $$"""
            public class LeftOut { }
            public struct S { public override string ToString() => "S"; }
            public struct Unboxed { public override string ToString() => "unboxed"; }
            public static class Box<T> where T : struct { public static object Run(T value) => value; public static Func<string> Bind(T value) => value.ToString; }
            public class Worker : BackgroundService
            {
                {{helper}}
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    {{operation}}
                    _ = new Unboxed();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddHostedService<Worker>();")));
        var run = await Run(solution, Mode.ConstructedTypes);
        Assert.False(run.Stopped);
        Assert.Contains("body:Fixture:M:S.ToString", run.Reachable.ReachedBodies.Keys);
        Assert.DoesNotContain("body:Fixture:M:Unboxed.ToString", run.Reachable.ReachedBodies.Keys);
    }

    [Theory]
    [InlineData(Mode.ConstructedTypes, false)]
    [InlineData(Mode.ClassHierarchy, true)]
    public async Task Generic_argument_without_boxing_does_not_admit_value_overrides(Mode mode, bool reached)
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + """
            public class LeftOut { }
            public struct S { public override string ToString() => "S"; }
            public class Worker : BackgroundService
            {
                static string Call<T>(T value) where T : struct => value.ToString();
                protected override Task ExecuteAsync(CancellationToken token)
                { Call(new S()); return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<Worker>();")));
        var run = await Run(solution, mode);
        Assert.False(run.Stopped);
        Assert.Equal(reached, run.Reachable.ReachedBodies.ContainsKey("body:Fixture:M:S.ToString"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_boxing_is_independent_of_argument_discovery_order(bool boxFirst)
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + $$"""
            public class LeftOut { }
            public struct S { public override string ToString() => "S"; }
            public struct Unboxed { public override string ToString() => "unboxed"; }
            public class Worker : BackgroundService
            {
                static object Box<T>(T value) where T : struct => value;
                static void Later() { Box(new S()).ToString(); }
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    {{(boxFirst ? "Box(1); Later();" : "Later(); Box(1);")}}
                    _ = new Unboxed();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddHostedService<Worker>();")));
        var run = await Run(solution, Mode.ConstructedTypes);
        Assert.False(run.Stopped);
        Assert.Contains("body:Fixture:M:S.ToString", run.Reachable.ReachedBodies.Keys);
        Assert.DoesNotContain("body:Fixture:M:Unboxed.ToString", run.Reachable.ReachedBodies.Keys);
    }

    [Theory]
    [InlineData("Base", false, false)]
    [InlineData("Base", true, false)]
    [InlineData("IConvert", false, false)]
    [InlineData("IConvert", true, false)]
    [InlineData("IConvert", false, true)]
    [InlineData("IConvert", true, true)]
    public async Task Dispatched_generic_boxing_binds_implementation_parameters(string receiverType, bool later, bool explicitImplementation)
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + $$"""
            public class LeftOut { }
            public struct S { public override string ToString() => "S"; }
            public struct Unboxed { public override string ToString() => "unboxed"; }
            public abstract class Base { public abstract object Convert<T>(T value); }
            public interface IConvert { object Convert<T>(T value); }
            public class Derived : Base, IConvert
            {
                public override object Convert<U>(U value) => value!;
                {{(explicitImplementation ? "object IConvert.Convert<V>(V value) => value!;" : "")}}
            }
            public class Worker : BackgroundService
            {
                private {{receiverType}} _receiver = null!;
                private void Make() { _receiver = new Derived(); }
                private void Call() { _receiver.Convert(new S()).ToString(); _ = new Unboxed(); }
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    {{(later ? "Call(); Make();" : "Make(); Call();")}}
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddHostedService<Worker>();")));
        var run = await Run(solution, Mode.ConstructedTypes);
        Assert.False(run.Stopped);
        Assert.Contains("body:Fixture:M:S.ToString", run.Reachable.ReachedBodies.Keys);
        Assert.DoesNotContain("body:Fixture:M:Unboxed.ToString", run.Reachable.ReachedBodies.Keys);
    }

    [Theory]
    [InlineData(false, false, Mode.ConstructedTypes)]
    [InlineData(true, false, Mode.ConstructedTypes)]
    [InlineData(false, true, Mode.ConstructedTypes)]
    [InlineData(true, true, Mode.ConstructedTypes)]
    [InlineData(false, false, Mode.ClassHierarchy)]
    [InlineData(true, false, Mode.ClassHierarchy)]
    [InlineData(false, true, Mode.ClassHierarchy)]
    [InlineData(true, true, Mode.ClassHierarchy)]
    public async Task Factory_receiver_preserves_each_opaque_implementation(bool mixed, bool throughDelegate, Mode mode)
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + $$"""
            public sealed class Arg { public int Count; }
            public sealed class Seen { public int Total; }
            public class Parent { public int BaseValue; }
            public sealed class Payload { public int Nested; }
            public interface IOp { void M(Arg arg, Action callback); }
            public class LeftOut : Parent, IOp
            {
                public int Value;
                public Payload Child = null!;
                public void M(Arg arg, Action callback) { Value = 1; BaseValue = 1; Child.Nested = 1; arg.Count = 1; callback(); }
            }
            public class Impl : IOp
            {
                public int Other;
                public void M(Arg arg, Action callback) { Other = 1; arg.Count = 2; callback(); }
            }
            public class Worker : BackgroundService
            {
                private readonly IOp _service;
                public Worker(IOp service) { _service = service; }
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    if (_service is LeftOut left) left.Child = new Payload();
                    var arg = new Arg();
                    var seen = new Seen();
                    Action callback = () => seen.Total = 1;
                    {{(throughDelegate ? "Action<Arg, Action> call = _service.M; call(arg, callback);" : "_service.M(arg, callback);")}}
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<IOp>(_ => " +
                          (mixed ? "Environment.TickCount > 0 ? new LeftOut() : new Impl()" : "new LeftOut()") +
                          "); services.AddHostedService<Worker>();")));
        var run = await Run(solution, mode);
        Assert.False(run.Stopped);
        var accesses = run.Collection.Accesses;
        var opaque = mode == Mode.ConstructedTypes;
        Assert.Equal(mixed, run.Heap.Instances.Values.Any(instance => instance.BodyId == "body:Fixture:M:Impl.M(Arg,System.Action)"));
        foreach (var member in new[] { "Count", "Value", "BaseValue", "Nested" })
            Assert.True(opaque == accesses.Any(access => access.Resource.Member.Name == member &&
                                                       access.Operation is AccessOperation.UnknownEffect or AccessOperation.Read),
                        member + ": " + string.Join("; ", accesses.Select(access => $"{access.Operation} {access.Resource.Member.Name} by {access.Symbol}")));
        Assert.Equal(opaque, accesses.Any(access => access.Resource.Member.Name == "Total" &&
                                                   run.Executions.Execution(access.ExecutionId).Kind == ExecutionKind.UnknownDelegateCall));
        Assert.DoesNotContain(accesses, access => access.Resource.Member.Name == "Other" &&
                                                  access.Operation is AccessOperation.UnknownEffect or AccessOperation.Read);
        Assert.Equal(!opaque, run.Heap.Instances.Values.Any(instance => instance.BodyId == "body:Fixture:M:LeftOut.M(Arg,System.Action)"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nonvirtual_delegate_uses_its_bound_member(bool virtualCall)
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + $$"""
            public class LeftOut { }
            public class Base { public virtual void M(Action work) { } }
            public class Derived : Base
            {
                public int Value;
                public override void M(Action work) { Value = 1; work(); }
                public void Run() { Action<Action> action = {{(virtualCall ? "this" : "base")}}.M; action(() => Value = 2); }
            }
            public class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token)
                { new Derived().Run(); return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<Worker>();")));
        var run = await Run(solution, Mode.ConstructedTypes);
        Assert.False(run.Stopped);
        Assert.DoesNotContain(run.Collection.Accesses, access => run.Executions.Execution(access.ExecutionId).Kind == ExecutionKind.UnknownDelegateCall);
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Resource.Member.Name == "Value" && access.Operation is AccessOperation.Read or AccessOperation.UnknownEffect);
        Assert.Equal(virtualCall, run.Collection.Accesses.Any(access => access.Resource.Member.Name == "Value" && access.Operation == AccessOperation.Write));
    }

    [Theory]
    [InlineData(Mode.ConstructedTypes, false)]
    [InlineData(Mode.ClassHierarchy, true)]
    public async Task Left_out_factory_override_never_runs_the_base_factory(Mode mode, bool runs)
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + """
            public class Service { public static int Value; }
            public class Factory
            {
                public virtual Service Make(IServiceProvider provider) { Service.Value = 1; return new Service(); }
            }
            public class LeftOut : Factory
            {
                public override Service Make(IServiceProvider provider) { Service.Value = 2; return new Service(); }
            }
            public class Worker(Service service) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token) { GC.KeepAlive(service); return Task.CompletedTask; }
            }
            """ + Startup("Factory f = new LeftOut(); services.AddSingleton<Service>(f.Make); services.AddHostedService<Worker>();")));
        var run = await Run(solution, mode);
        Assert.False(run.Stopped);
        Assert.DoesNotContain(run.Heap.Instances.Values, instance => instance.BodyId == "body:Fixture:M:Factory.Make(System.IServiceProvider)");
        Assert.Equal(runs, run.Collection.Accesses.Any(access => access.Resource.Member.Name == "Value" && access.Operation == AccessOperation.Write));
    }

    // ---- the rows ----

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Cell_gets_its_expected_answer(Operation operation, Making making, Timing timing, Mode mode)
    {
        var cell = new Cell(operation, making, timing, mode);
        var expected = Expected(cell);
        var program = Program(cell);
        var compilation = (await program.Projects.Single().GetCompilationAsync())!;
        var run = await Run(program, mode);
        Assert.False(run.Stopped);
        var failures = new List<string>();
        void Check(bool holds, string what)
        {
            if (!holds)
                failures.Add(what);
        }

        var callee = CalleeType(cell);
        var calleeBodies = Bodies(compilation, callee, cell);
        var instances = run.Heap.Instances.Values.Select(instance => instance.BodyId).ToHashSet(StringComparer.Ordinal);
        var accesses = run.Collection.Accesses;
        ExecutionKind KindOf(Access access) => run.Executions.Execution(access.ExecutionId).Kind;
        bool InRegionOf(Access access, string type) =>
            access.Resource.RegionId is { } region && run.Heap.Regions.TryGetValue(region, out var found) &&
            found.TypeKey == SymbolNames.TypeKey(compilation.GetTypeByMetadataName(type)!);
        bool Writes(string member, string symbolPrefix) =>
            accesses.Any(access => access.Operation == AccessOperation.Write && access.Resource.Member.Name == member &&
                                   access.Symbol.StartsWith(symbolPrefix, StringComparison.Ordinal));
        // An unknown effect on an object of one execution only reads it; no code of the program reads Count or Value, so a read of
        // either is the effect too.
        bool Unknown(string member, Func<Access, bool>? where = null) =>
            accesses.Any(access => access.Operation is AccessOperation.UnknownEffect or AccessOperation.Read && access.Resource.Member.Name == member &&
                                   (where?.Invoke(access) ?? true));
        bool LambdaRunsIn(Func<ExecutionKind, bool> kind) =>
            accesses.Any(access => access.Operation == AccessOperation.Write && access.Resource.Member.Name == "Total" && kind(KindOf(access)));

        // The callee's bodies: reached, and bound to the operation.
        foreach (var body in calleeBodies)
        {
            Check(run.Reachable.ReachedBodies.ContainsKey(body) == expected.Reached, $"{body} is {(expected.Reached ? "not " : "")}in the reachable set");
            Check(instances.Contains(body) == expected.Bound, $"{body} is {(expected.Bound ? "not " : "")}bound by the heap");
        }

        var carried = Carried(cell);
        if (expected.Bound)
        {
            // The callee touches each channel itself: the argument, its receiver, the `out` argument, and the lambda it invokes.
            var touched = $"{callee}.";
            if (carried.HasFlag(Channel.Argument))
                Check(Writes("Count", touched), $"{callee} does not write the argument");
            if (carried.HasFlag(Channel.Receiver) && cell.Operation != Operation.Constructor)
            {
                Check(IsSpawn(cell.Operation)
                          ? accesses.Any(access => access.Operation == AccessOperation.Write && access.Resource.Member.Name == "Value" &&
                                                   access.Symbol.StartsWith(touched, StringComparison.Ordinal) && KindOf(access) == ExecutionKind.Spawn)
                          : Writes("Value", touched),
                      $"{callee} does not write its receiver{(IsSpawn(cell.Operation) ? " in the spawn" : "")}");
            }
            if (carried.HasFlag(Channel.Out))
                Check(Writes("Out", touched), $"{callee} does not write the out argument");
            if (carried.HasFlag(Channel.Lambda))
                Check(LambdaRunsIn(kind => kind != ExecutionKind.UnknownDelegateCall), "the lambda the callee invokes does not run");
        }

        if (OtherType(cell) is { } other)
        {
            var otherBodies = Bodies(compilation, other, cell);
            foreach (var body in otherBodies)
            {
                Check(run.Reachable.ReachedBodies.ContainsKey(body) == expected.OtherBound, $"{body} is {(expected.OtherBound ? "not " : "")}in the reachable set");
                Check(instances.Contains(body) == expected.OtherBound, $"{body} is {(expected.OtherBound ? "not " : "")}bound by the heap");
            }
            if (expected.OtherBound && carried.HasFlag(Channel.Out) && cell.Making == Making.LeftOutMixed)
                Check(Writes("Out", $"{other}."), $"{other} does not write the out argument");
        }

        if (expected.Opaque)
        {
            // The resolution into the callee is opaque, each channel touched from where the operation stands (R3).
            var channels = OpaqueChannels(cell);
            if (channels.HasFlag(Channel.Argument))
                Check(Unknown("Count"), "the opaque call has no unknown effect on its argument");
            if (channels.HasFlag(Channel.Receiver))
                Check(Unknown("Value", access => InRegionOf(access, callee)), $"the opaque call has no unknown effect on its {callee} receiver");
            if (channels.HasFlag(Channel.Out))
                Check(Writes("Out", "Worker."), "the out argument is not written where the operation stands");
            if (channels.HasFlag(Channel.Lambda))
                Check(LambdaRunsIn(kind => kind == ExecutionKind.UnknownDelegateCall), "the handed lambda does not run in an unknown execution");
            // A constructor without a body does not touch the object it creates; a dispatch names the in-set declaration, so an
            // unresolved one writes no `out` argument where it stands.
            if (cell.Operation == Operation.Constructor)
                Check(!Unknown("Value", access => InRegionOf(access, callee)), "the opaque constructor touches the object it creates");
            if (carried.HasFlag(Channel.Out) && !NamesTheCallee(cell.Operation) && cell.Making != Making.Unboxed)
                Check(!Writes("Out", "Worker."), "an unresolved dispatch writes the out argument where it stands");
        }
        else
        {
            Check(!Unknown("Count") && !Unknown("Value"), "an operation that resolves no opaque call has an unknown effect");
            Check(!LambdaRunsIn(kind => kind == ExecutionKind.UnknownDelegateCall), "a lambda runs in an unknown execution");
        }

        if (expected.Unseen)
        {
            Check(!accesses.Any(access => access.Symbol.StartsWith($"{LEFT_OUT}.", StringComparison.Ordinal)), "a body of the left-out type runs");
            Check(!Unknown("Value", access => InRegionOf(access, LEFT_OUT)), "the work's object gets an effect");
            if (cell.Operation == Operation.SpawnDelegate)
                Check(run.Heap.TaskCompleters.Values.Any(completers => completers.Any(completer => completer.Kind == TaskCompleterKind.Unseen)),
                      "the spawn's task does not complete unseen");
        }

        if (mode == Mode.ConstructedTypes)
        {
            // R2: no body of the left-out type — method, accessor, constructor, type initializer or the lambda one holds — is reached,
            // activated or run.
            var leftOut = $":{LEFT_OUT}.";
            Check(!run.Reachable.ReachedBodies.Keys.Any(body => body.Contains(leftOut, StringComparison.Ordinal)), "a body of the left-out type is reached");
            Check(!instances.Any(body => body.Contains(leftOut, StringComparison.Ordinal)), "a body of the left-out type is bound");
            Check(!accesses.Any(access => access.Resource.Member.Name == "Own"), "the lambda a left-out member holds runs");
        }

        var observed = accesses.Where(access => access.Resource.Member.Name is "Count" or "Value" or "Out" or "Total")
                               .Select(access => $"  {access.Operation} {access.Resource.Member.Name} in {access.Resource.Region} by {access.Symbol} ({KindOf(access)})");
        Assert.True(failures.Count == 0, $"{cell} ({expected.Reason}):\n{string.Join("\n", failures)}\naccesses:\n{string.Join("\n", observed)}");
    }

    // ---- the facts ----

    [Fact]
    public void No_cell_outside_the_known_gap_narrows_unsafely()
    {
        Assert.Empty(KnownWider);
        var cells = Cells().ToArray();
        Assert.All(KnownGap, cell => Assert.Contains(cell, cells));
        foreach (var cell in cells.Where(HasReceiverObject))
        {
            var expected = Expected(cell);
            if (KnownGap.Contains(cell))
                Assert.True(expected is { Bound: false, Opaque: false, Unseen: true }, $"{cell} is listed as a gap but is not one");
            else
                Assert.True(expected.Bound || expected.Opaque, $"{cell} binds its callee nowhere and is not opaque");
        }
    }

    [Fact]
    public void Every_cell_has_a_row_and_an_expected_answer()
    {
        var cells = Cells().ToArray();
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.Equal(cells, Rows().Select(row => new Cell((Operation)row[0], (Making)row[1], (Timing)row[2], (Mode)row[3])));
        Assert.All(cells, cell => Assert.False(string.IsNullOrEmpty(Expected(cell).Reason)));
        // 27 cells of the operations that do not dispatch, 20 each of the virtual and the interface call, 18 each of the delegates on a
        // virtual or interface method and the spawns.
        Assert.Equal(27 + 2 * 20 + 4 * 18, cells.Length);
    }

    [Fact]
    public void Every_value_of_every_axis_occurs_in_some_row()
    {
        var cells = Cells().ToArray();
        Assert.All(Enum.GetValues<Operation>(), value => Assert.Contains(cells, cell => cell.Operation == value));
        Assert.All(Enum.GetValues<Making>(), value => Assert.Contains(cells, cell => cell.Making == value));
        Assert.All(Enum.GetValues<Timing>(), value => Assert.Contains(cells, cell => cell.Timing == value));
        Assert.All(Enum.GetValues<Mode>(), value => Assert.Contains(cells, cell => cell.Mode == value));
    }

    // ---- the program ----

    /// <summary>The type that declares the callee.</summary>
    /// <param name="cell">The cell.</param>
    private static string CalleeType(Cell cell) => cell.Making switch
    {
        Making.LeftOut or Making.LeftOutMixed => LEFT_OUT,
        Making.Boxed or Making.Unboxed => "ImplS",
        _ => cell.Operation switch
        {
            Operation.Static or Operation.StaticField or Operation.DelegateStatic => "Lib",
            Operation.Constructor => "Made",
            _ => "Impl"
        }
    };

    /// <summary>The type of the other receiver whose implementation the operation may bind, or null.</summary>
    /// <param name="cell">The cell.</param>
    private static string? OtherType(Cell cell) => cell.Making switch
    {
        Making.NeverConstructed when IsDispatching(cell.Operation) => "Other",
        Making.LeftOutMixed => "Impl",
        _ => null
    };

    /// <summary>The body ids of the members of <paramref name="type"/> the cell's operation runs.</summary>
    /// <param name="compilation">The program's compilation.</param>
    /// <param name="type">The declaring type.</param>
    /// <param name="cell">The cell.</param>
    private static IReadOnlyList<string> Bodies(Compilation compilation, string type, Cell cell)
    {
        var symbol = compilation.GetTypeByMetadataName(type)!;
        IEnumerable<IMethodSymbol> Named(string name) => symbol.GetMembers(name).OfType<IMethodSymbol>();
        IEnumerable<IMethodSymbol> methods = cell.Operation switch
        {
            Operation.Static or Operation.DelegateStatic => Named("S"),
            Operation.StaticField => symbol.StaticConstructors,
            Operation.Property => Named("get_P").Concat(Named("set_P")),
            Operation.Instance or Operation.DelegateInstance => Named("N"),
            Operation.Constructor => symbol.InstanceConstructors.Where(constructor => constructor.Parameters.Length == 3),
            Operation.Virtual or Operation.DelegateVirtual when type == "ImplS" => Named("Equals").Where(method => method.Parameters.Length == 1),
            Operation.Virtual or Operation.Interface or Operation.DelegateVirtual or Operation.DelegateInterface => Named("M"),
            Operation.SpawnDelegate => Named(type == "ImplS" ? "ToString" : "Work"),
            Operation.SpawnMethod => Named("Execute"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        var bodies = methods.Select(IrLowering.RootBodyId).ToArray();
        Assert.NotEmpty(bodies);
        return bodies;
    }

    /// <summary>The statements of <c>Operate</c> and of <c>Make</c>, over the locals <c>work</c> (the lambda) and the fields <c>_arg</c>,
    /// <c>_sink</c>, <c>_impl</c> and the slots an <c>after</c> making fills.</summary>
    /// <param name="cell">The cell.</param>
    private static (string Operate, string Make) Code(Cell cell)
    {
        const string ARGUMENTS = "_arg, work, out _sink.Out";
        var after = cell.Timing == Timing.After;
        var make = !after ? "" : cell.Making switch
        {
            Making.Constructed => "new Impl()",
            Making.DerivedConstructed => "new Derived()",
            Making.Boxed => "new ImplS()",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        string Slot(string slot) => $"{slot} = {make};";
        string Receiver(string type) => cell.Making switch
        {
            Making.Constructed => "new Impl()",
            Making.DerivedConstructed => "new Derived()",
            Making.RootConstructed => "_impl",
            Making.NeverConstructed => "new Other()",
            Making.LeftOut => $"new {LEFT_OUT}()",
            Making.LeftOutMixed => $"Environment.TickCount > 0 ? new {LEFT_OUT}() : ({type})new Impl()",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        var callee = CalleeType(cell);
        var value = IsValueType(cell.Making);
        return cell.Operation switch
        {
            Operation.Static => ($"{callee}.S({ARGUMENTS});", ""),
            Operation.StaticField => ($"_ = {callee}.Field;", ""),
            Operation.Property => ($"{callee} r = {Receiver(callee)}; r.P = _arg; _ = r.P;", ""),
            Operation.Instance => ($"{callee} r = {Receiver(callee)}; r.N({ARGUMENTS});", ""),
            Operation.Constructor => ($"_ = new {callee}({ARGUMENTS});", ""),
            Operation.DelegateStatic => ($"Handler d = {callee}.S; d({ARGUMENTS});", ""),
            Operation.DelegateInstance => ($"{callee} r = {Receiver(callee)}; Handler d = r.N; d({ARGUMENTS});", ""),

            Operation.Virtual when cell.Making == Making.Unboxed => ("var s = new ImplS(); Constrained.Equal(s, _arg);", ""),
            Operation.Virtual when value => after ? ("object r = _object!; r.Equals(_arg);", Slot("_object")) : ("object r = new ImplS(); r.Equals(_arg);", ""),
            Operation.Virtual => after ? ($"Base r = _base!; r.M({ARGUMENTS});", Slot("_base")) : ($"Base r = {Receiver("Base")}; r.M({ARGUMENTS});", ""),

            Operation.Interface when cell.Making == Making.Unboxed => ($"var s = new ImplS(); Constrained.Call(s, {ARGUMENTS});", ""),
            Operation.Interface when value && !after => ($"IOp r = new ImplS(); r.M({ARGUMENTS});", ""),
            Operation.Interface => after ? ($"IOp r = _op!; r.M({ARGUMENTS});", Slot("_op")) : ($"IOp r = {Receiver("IOp")}; r.M({ARGUMENTS});", ""),

            // Before, only the delegate's creation on the value's override boxes it.
            Operation.DelegateVirtual when value => after
                ? ("object r = _object!; Func<object?, bool> d = r.Equals; d(_arg);", Slot("_object"))
                : ("var s = new ImplS(); Func<object?, bool> d = s.Equals; d(_arg);", ""),
            Operation.DelegateVirtual => after
                ? ($"Base r = _base!; Handler d = r.M; d({ARGUMENTS});", Slot("_base"))
                : ($"Base r = {Receiver("Base")}; Handler d = r.M; d({ARGUMENTS});", ""),

            Operation.DelegateInterface when value && !after => ($"IOp r = new ImplS(); Handler d = r.M; d({ARGUMENTS});", ""),
            Operation.DelegateInterface => after
                ? ($"IOp r = _op!; Handler d = r.M; d({ARGUMENTS});", Slot("_op"))
                : ($"IOp r = {Receiver("IOp")}; Handler d = r.M; d({ARGUMENTS});", ""),

            // Before, only the delegate's creation on the value's override boxes it.
            Operation.SpawnDelegate when value => after
                ? ("object r = _object!; Task.Run(r.ToString);", Slot("_object"))
                : ("var s = new ImplS(); Task.Run(s.ToString);", ""),
            Operation.SpawnDelegate => after ? ("Base r = _base!; Task.Run(r.Work);", Slot("_base")) : ($"Base r = {Receiver("Base")}; Task.Run(r.Work);", ""),

            Operation.SpawnMethod when value && !after => ("ThreadPool.UnsafeQueueUserWorkItem(new ImplS(), false);", ""),
            Operation.SpawnMethod => after
                ? ("ThreadPool.UnsafeQueueUserWorkItem(_item!, false);", Slot("_item"))
                : ($"IThreadPoolWorkItem r = {Receiver("IThreadPoolWorkItem")}; ThreadPool.UnsafeQueueUserWorkItem(r, false);", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
    }

    /// <summary>The program of a cell, compiled once for both modes.</summary>
    /// <param name="cell">The cell.</param>
    private static Solution Program(Cell cell)
    {
        var source = Source(cell);
        return Programs.GetOrAdd(source, text => new Lazy<Solution>(() => FixtureSolution.Create(("Case.cs", text)))).Value;
    }

    /// <summary>The source of a cell: the callee types, and a hosted service <c>Worker</c> that runs <c>Operate</c> and then
    /// <c>Make</c>; a root-constructed row's <c>Worker</c> takes a DI singleton <c>Impl</c>.</summary>
    /// <param name="cell">The cell.</param>
    private static string Source(Cell cell)
    {
        var (operate, make) = Code(cell);
        var root = cell.Making == Making.RootConstructed;
        var constructor = root ? "private readonly Impl _impl; public Worker(Impl impl) => _impl = impl;" : "";
        var registrations = (root ? "services.AddSingleton<Impl>(); " : "") + "services.AddHostedService<Worker>();";
        return Usings + $$"""
            public sealed class Arg { public int Count; }
            public sealed class Sink { public int Out; }
            public sealed class Seen { public int Total; }
            public static class Totals { public static int Init; public static int Own; }

            public delegate void Handler(Arg arg, Action work, out int value);
            public interface IOp { void M(Arg arg, Action work, out int value); }
            public abstract class Base
            {
                public abstract void M(Arg arg, Action work, out int value);
                public abstract void Work();
            }

            public static class Lib
            {
                public static int Field;
                static Lib() { Totals.Init = 1; }
                public static void S(Arg arg, Action work, out int value) { arg.Count = 1; work(); value = 1; }
            }

            public sealed class Made
            {
                public int Value;
                public Made(Arg arg, Action work, out int value) { arg.Count = 1; Value = 1; work(); value = 1; }
            }

            public class Impl : Base, IOp, IThreadPoolWorkItem
            {
                public int Value;
                public override void M(Arg arg, Action work, out int value) { arg.Count = 1; Value = 1; work(); value = 1; }
                public override void Work() { Value = 1; }
                public void Execute() { Value = 1; }
                public void N(Arg arg, Action work, out int value) { arg.Count = 1; Value = 1; work(); value = 1; }
                public Arg P { get { Value = 1; return null!; } set { value.Count = 1; Value = 1; } }
            }

            public sealed class Derived : Impl { }

            public sealed class Other : Base, IOp, IThreadPoolWorkItem
            {
                public int Value;
                public override void M(Arg arg, Action work, out int value) { arg.Count = 1; Value = 1; work(); value = 1; }
                public override void Work() { Value = 1; }
                public void Execute() { Value = 1; }
            }

            public struct ImplS : IOp, IThreadPoolWorkItem
            {
                public int Value;
                public void M(Arg arg, Action work, out int value) { arg.Count = 1; Value = 1; work(); value = 1; }
                public override bool Equals(object? obj) { ((Arg)obj!).Count = 1; Value = 1; return false; }
                public override int GetHashCode() => 0;
                public override string ToString() { Value = 1; return ""; }
                public void Execute() { Value = 1; }
            }

            public static class Constrained
            {
                public static bool Equal<T>(T value, Arg arg) where T : struct => value.Equals(arg);
                public static void Call<T>(T value, Arg arg, Action work, out int result) where T : struct, IOp => value.M(arg, work, out result);
            }

            public class {{LEFT_OUT}} : Base, IOp, IThreadPoolWorkItem
            {
                public static int Field;
                public int Value;
                static {{LEFT_OUT}}() { Action own = () => Totals.Own = 1; own(); Totals.Init = 1; }
                public {{LEFT_OUT}}() { Action own = () => Totals.Own = 1; own(); }
                public {{LEFT_OUT}}(Arg arg, Action work, out int value) { Action own = () => Totals.Own = 1; own(); arg.Count = 1; Value = 1; work(); value = 1; }
                public static void S(Arg arg, Action work, out int value) { Action own = () => Totals.Own = 1; own(); arg.Count = 1; work(); value = 1; }
                public override void M(Arg arg, Action work, out int value) { Action own = () => Totals.Own = 1; own(); arg.Count = 1; Value = 1; work(); value = 1; }
                public override void Work() { Action own = () => Totals.Own = 1; own(); Value = 1; }
                public void Execute() { Action own = () => Totals.Own = 1; own(); Value = 1; }
                public void N(Arg arg, Action work, out int value) { Action own = () => Totals.Own = 1; own(); arg.Count = 1; Value = 1; work(); value = 1; }
                public Arg P
                {
                    get { Action own = () => Totals.Own = 1; own(); Value = 1; return null!; }
                    set { Action own = () => Totals.Own = 1; own(); value.Count = 1; Value = 1; }
                }
            }

            public sealed class Worker : BackgroundService
            {
                private readonly Arg _arg = new();
                private readonly Sink _sink = new();
                private readonly Seen _seen = new();
                private Base? _base;
                private IOp? _op;
                private IThreadPoolWorkItem? _item;
                private object? _object;
                {{constructor}}

                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    Operate();
                    Make();
                    return Task.CompletedTask;
                }

                private void Operate()
                {
                    var seen = _seen;
                    Action work = () => seen.Total = 1;
                    {{operate}}
                }

                private void Make()
                {
                    {{make}}
                }
            }
            """ + Startup(registrations);
    }

    /// <summary>Runs the program's one scope through the pipeline under the mode's reachability rules.</summary>
    /// <param name="solution">The program.</param>
    /// <param name="mode">The dispatch rule; constructed types leave <see cref="LEFT_OUT"/> out.</param>
    private static async Task<ScopeRun> Run(Solution solution, Mode mode)
    {
        var scoped = Assert.Single(ProcessScopes.Discover(solution, ROOT_DIRECTORY).Scopes);
        var projectFiles = new List<(Compilation Compilation, string? ProjectFilePath)>();
        foreach (var project in scoped.Projects)
            projectFiles.Add(((await project.GetCompilationAsync())!, project.FilePath));
        var compilations = projectFiles.Select(project => project.Compilation).ToArray();
        var leftOut = SymbolNames.TypeKey(compilations.Single().GetTypeByMetadataName(LEFT_OUT)!);
        var rules = mode == Mode.ConstructedTypes
            ? new ReachabilityRules(DispatchRule.ConstructedTypes, new HashSet<string>(StringComparer.Ordinal) { leftOut })
            : null;
        return ScopePipeline.Run(scoped.Scope.Id, compilations, projectFiles, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, LibraryModels.BuiltIn,
                                 AnalysisLimits.Default, new(), null, CancellationToken.None, reachability: rules);
    }
}
