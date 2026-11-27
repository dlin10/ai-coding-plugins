using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of the grid of partial members (issue 513), each applied to a form the analysis already answers right — two
/// hosted services writing one field through one member of a singleton's object — so that the partial member is the only new thing.
/// A partial member has one symbol, its defining part, that every call, reference and delegate binds to, and the compiler emits one
/// member whose body is the implementing part's (C# specification, partial methods; the feature specifications of partial properties
/// and indexers, C# 13, and of partial constructors and events, C# 14): a call to a partial method runs that body wherever the part
/// is declared, in the same file, another file or a file marked <c>&lt;auto-generated/&gt;</c>, whatever its return type, <c>out</c>
/// parameters, accessibility, static or generic shape; a partial property or indexer is a member with the implementing part's
/// accessors and no backing field of its own unless they use <c>field</c>; a partial constructor runs the type's field initializers
/// and its implementing body; a partial event's accessors are the implementing part's, so it is no field-like event. A partial method
/// with no implementing part (one with no accessibility, <c>void</c> and no <c>out</c> parameter) is removed with every call to it,
/// the evaluation of its arguments included. An implementing part may name its parameters and type parameters otherwise than the
/// defining part; they are still the member's, by ordinal. Every probe runs through the whole analysis (<see cref="HostedServiceProbe"/>)
/// and names each finding's pair of sides; a probe the analysis answers otherwise is named in <see cref="Differences"/> with its answer
/// and why.</summary>
public sealed class PartialMemberProbeTests
{
    /// <summary>A meter with a value and the definitions of its partial methods.</summary>
    private const string METER = """
        public sealed partial class Meter
        {
            public int Value;
            public static int Total;
            public partial void Bump();
            public partial int Next();
            public partial bool TryBump(out int value);
            private partial void BumpCore();
            public void BumpPrivately() => BumpCore();
            public static partial void BumpTotal();
            public partial void Add<T>(T item);
            partial void Log(int value);
            public void LogNext() => Log(Value++);
        }

        """;

    /// <summary>The part of <see cref="METER"/> that implements its partial methods, save <c>Log</c>, which has none.</summary>
    private const string METER_IMPLEMENTATION = """
        public sealed partial class Meter
        {
            public partial void Bump() { Value++; }
            public partial int Next() => ++Value;
            public partial bool TryBump(out int value) { value = ++Value; return true; }
            private partial void BumpCore() { Value++; }
            public static partial void BumpTotal() { Total++; }
            public partial void Add<T>(T item) { Value++; }
            public void BumpFromHere() => Bump();
        }

        """;

    /// <summary>The singleton holding a meter.</summary>
    private const string HOLDER = "public sealed class Holder { private readonly Meter _meter = new(); public void Run() => {0}; }\n";

    /// <summary>The lost update of <c>Value</c> in two runs of <paramref name="member"/>.</summary>
    /// <param name="member">The member that increments it.</param>
    private static string ValueIncrementedIn(string member) => $"DCA1002 unprotected Value: ReadModifyWrite {member} | ReadModifyWrite {member}";

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them,
    /// and the further files of its project.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth, (string Path, string Source)[] Files)> Probes =
        new(StringComparer.Ordinal)
        {
            ["method-implemented-in-another-file"] = (Both(METER + Holder("_meter.Bump()")), [ValueIncrementedIn("Meter.Bump")],
                                                      [("Meter.Parts.cs", METER_IMPLEMENTATION)]),
            ["method-returning-a-value"] = (Both(METER + METER_IMPLEMENTATION + Holder("_ = _meter.Next()")), [ValueIncrementedIn("Meter.Next")], []),
            ["method-with-an-out-parameter"] = (Both(METER + METER_IMPLEMENTATION + Holder("_meter.TryBump(out _)")),
                                                [ValueIncrementedIn("Meter.TryBump")], []),
            ["private-method-called-inside-its-type"] = (Both(METER + METER_IMPLEMENTATION + Holder("_meter.BumpPrivately()")),
                                                         [ValueIncrementedIn("Meter.BumpCore")], []),
            ["static-method"] = (Both(METER + METER_IMPLEMENTATION + Holder("Meter.BumpTotal()")),
                                 ["DCA1002 unprotected Total: ReadModifyWrite Meter.BumpTotal | ReadModifyWrite Meter.BumpTotal"], []),
            ["generic-method"] = (Both(METER + METER_IMPLEMENTATION + Holder("_meter.Add(\"x\")")), [ValueIncrementedIn("Meter.Add<T>")], []),
            ["method-called-from-its-implementing-part"] = (Both(METER + METER_IMPLEMENTATION + Holder("_meter.BumpFromHere()")),
                                                            [ValueIncrementedIn("Meter.Bump")], []),
            ["method-invoked-through-a-delegate"] = (Both(METER + METER_IMPLEMENTATION +
                "public sealed class Holder { private readonly Meter _meter = new(); public void Run() { Action bump = _meter.Bump; bump(); } }\n"),
                [ValueIncrementedIn("Meter.Bump")], []),
            ["method-of-a-generic-type"] = (Both("""
                public sealed partial class Bag<T> { public int Count; public partial void Put(T item); }
                public sealed partial class Bag<T> { public partial void Put(T item) { Count++; } }
                public sealed class Holder { private readonly Bag<string> _bag = new(); public void Run() => _bag.Put("x"); }
                """), ["DCA1002 unprotected Count: ReadModifyWrite Bag<T>.Put | ReadModifyWrite Bag<T>.Put"], []),
            ["virtual-method"] = (Both("""
                public partial class Meter { public int Value; public virtual partial void Bump(); }
                public partial class Meter { public virtual partial void Bump() { Value++; } }
                public sealed class Holder { private readonly Meter _meter = new(); public void Run() => _meter.Bump(); }
                """), [ValueIncrementedIn("Meter.Bump")], []),
            ["method-without-implementation-drops-its-arguments"] = (Both(METER + METER_IMPLEMENTATION + Holder("_meter.LogNext()")), [], []),
            ["property-getter"] = (Both("""
                public sealed partial class Gauge { public partial int Reads { get; } }
                public sealed partial class Gauge { private int _reads; public partial int Reads { get => ++_reads; } }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Run() => _ = _gauge.Reads; }
                """), ["DCA1002 unprotected _reads: ReadModifyWrite Gauge.get_Reads | ReadModifyWrite Gauge.get_Reads"], []),
            ["property-read-against-a-write-of-its-field"] = (new("Holder", """
                public sealed partial class Gauge { public partial int Level { get; set; } }
                public sealed partial class Gauge { private int _level; public partial int Level { get => _level; set => _level = value; } public void Reset() => _level = 0; }
                public sealed class Holder { private readonly Gauge _gauge = new(); public int Read() => _gauge.Level; public void Reset() => _gauge.Reset(); }
                """, "_ = _holder.Read();", "_holder.Reset();"), ["DCA1001 unprotected _level: Read Gauge.get_Level | Write Gauge.Reset"], []),
            ["property-set-inside-its-implementing-part"] = (new("Holder", """
                public sealed partial class Gauge { public partial int Level { get; set; } }
                public sealed partial class Gauge { private int _level; public partial int Level { get => _level; set => _level = value; } public void Raise() => Level = 2; }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Run() => _gauge.Raise(); }
                """, "_holder.Run();", "_holder.Run();"), ["DCA1001 unprotected _level: Write Gauge.set_Level | Write Gauge.set_Level"], []),
            ["property-init-accessor"] = (Both("""
                public sealed partial class Gauge { public static int Created; public partial int Level { get; init; } }
                public sealed partial class Gauge { private int _level; public partial int Level { get => _level; init { _level = value; Created++; } } }
                public sealed class Holder { public void Run() => _ = new Gauge { Level = 1 }; }
                """), ["DCA1002 unprotected Created: ReadModifyWrite Gauge.set_Level | ReadModifyWrite Gauge.set_Level"], []),
            ["property-over-its-field-keyword"] = (Both("""
                public sealed partial class Gauge { public partial int Level { get; set; } }
                public sealed partial class Gauge { public partial int Level { get => field; set => field = value; } }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Run() => _gauge.Level = 1; }
                """), ["DCA1001 unprotected <Level>k__BackingField: Write Gauge.set_Level | Write Gauge.set_Level"], []),
            ["static-property"] = (Both("""
                public sealed partial class Gauge { public static partial int Shared { get; set; } }
                public sealed partial class Gauge { private static int s_shared; public static partial int Shared { get => s_shared; set => s_shared = value; } }
                public sealed class Holder { public void Run() => Gauge.Shared = 1; }
                """), ["DCA1001 unprotected s_shared: Write Gauge.set_Shared | Write Gauge.set_Shared"], []),
            ["property-implemented-in-a-generated-file"] = (Both("""
                public sealed partial class Gauge { public partial int Level { get; set; } }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Run() => _gauge.Level = 1; }
                """), ["DCA1001 unprotected _level: Write Gauge.set_Level | Write Gauge.set_Level"],
                [("Gauge.g.cs", "// <auto-generated/>\npublic sealed partial class Gauge { private int _level; public partial int Level { get => _level; set => _level = value; } }\n")]),
            ["indexer"] = (Both("""
                public sealed partial class Gauge { public partial int this[int index] { get; set; } }
                public sealed partial class Gauge { private int _last; public partial int this[int index] { get => _last; set => _last = value; } }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Run() => _gauge[0] = 1; }
                """), ["DCA1001 unprotected _last: Write Gauge.set_Item | Write Gauge.set_Item"], []),
            ["constructor"] = (Both("""
                public sealed partial class Gauge { public static int Created; public partial Gauge(); }
                public sealed partial class Gauge { public partial Gauge() { Created++; } }
                public sealed class Holder { public void Run() => _ = new Gauge(); }
                """), ["DCA1002 unprotected Created: ReadModifyWrite Gauge..ctor | ReadModifyWrite Gauge..ctor"], []),
            ["constructor-runs-field-initializers"] = (Both("""
                public sealed partial class Gauge { public static int Created; public int Id = Next(); private static int Next() => ++Created; public partial Gauge(); }
                public sealed partial class Gauge { public partial Gauge() { } }
                public sealed class Holder { public void Run() => _ = new Gauge(); }
                """), ["DCA1002 unprotected Created: ReadModifyWrite Gauge.Next | ReadModifyWrite Gauge.Next"], []),
            ["type-parameter-renamed-by-the-implementation"] = (new("Holder", """
                public static class Storage<T> { public static int Count; }
                public sealed partial class Meter { public partial void Put<T>(); }
                public sealed partial class Meter { public partial void Put<U>() { Storage<U>.Count++; } }
                public sealed class Holder { private readonly Meter _meter = new(); public void Run() => _meter.Put<string>(); public void Direct() => Storage<string>.Count++; }
                """, "_holder.Run();", "_holder.Direct();"),
                ["DCA1002 unprotected Count: ReadModifyWrite Holder.Direct | ReadModifyWrite Meter.Put<T>"], []),
            ["type-parameter-renamed-on-two-instantiations"] = (new("Holder", """
                public static class Storage<T> { public static int Count; }
                public sealed partial class Meter { public partial void Put<T>(); }
                public sealed partial class Meter { public partial void Put<U>() { Storage<U>.Count++; } }
                public sealed class Holder { private readonly Meter _meter = new(); public void Text() => _meter.Put<string>(); public void Number() => _meter.Put<int>(); }
                """, "_holder.Text();", "_holder.Number();"), [], []),
            ["parameter-renamed-by-the-implementation"] = (Both("""
                public sealed partial class Meter { public partial void Add(Box box); }
                public sealed partial class Meter { public partial void Add(Box target) { target.Value++; } }
                public sealed class Holder { private readonly Meter _meter = new(); private readonly Box _box = new(); public void Run() => _meter.Add(_box); }
                """), [ValueIncrementedIn("Meter.Add")], []),
            ["generic-unimplemented-method-drops-its-arguments"] = (Both("""
                public sealed partial class Meter { public int Count; partial void Trace<T>(T item); public void Bump() => Trace(++Count); }
                public sealed class Holder { private readonly Meter _meter = new(); public void Run() => _meter.Bump(); }
                """), [], []),
            ["initializer-stores-the-backing-field"] = (Both("""
                public sealed partial class Gauge { public static int Sets; public partial int Level { get; set; } }
                public sealed partial class Gauge { public partial int Level { get => field; set { Sets++; field = value; } } = 7; }
                public sealed class Holder { public void Run() => _ = new Gauge(); }
                """), [], []),
            ["plain-initializer-stores-the-backing-field"] = (Both("""
                public sealed class Gauge { public static int Sets; public int Level { get => field; set { Sets++; field = value; } } = 7; }
                public sealed class Holder { public void Run() => _ = new Gauge(); }
                """), [], []),
            ["get-only-initializer-aliases-a-shared-object"] = (new("Holder", """
                public sealed partial class Gauge { public static readonly Box Shared = new(); public partial Box Current { get; } }
                public sealed partial class Gauge { public partial Box Current { get => field; } = Shared; }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Bump() => _gauge.Current.Value++; public void Direct() => Gauge.Shared.Value++; }
                """, "_holder.Bump();", "_holder.Direct();"), ["DCA1002 unprotected Value: ReadModifyWrite Holder.Bump | ReadModifyWrite Holder.Direct"], []),
            ["plain-get-only-initializer-aliases-shared-object"] = (new("Holder", """
                public sealed class Gauge { public static readonly Box Shared = new(); public Box Current { get => field; } = Shared; }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Bump() => _gauge.Current.Value++; public void Direct() => Gauge.Shared.Value++; }
                """, "_holder.Bump();", "_holder.Direct();"), ["DCA1002 unprotected Value: ReadModifyWrite Holder.Bump | ReadModifyWrite Holder.Direct"], []),
            ["semicolon-accessor-beside-field"] = (Both("""
                public sealed partial class Gauge { public partial int Level { get; set; } }
                public sealed partial class Gauge { public partial int Level { get => field; set; } }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Run() => _gauge.Level = 1; }
                """), ["DCA1001 unprotected <Level>k__BackingField: Write Gauge.set_Level | Write Gauge.set_Level"], []),
            ["plain-semicolon-accessor-beside-field"] = (Both("""
                public sealed class Gauge { public int Level { get => field; set; } }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Run() => _gauge.Level = 1; }
                """), ["DCA1001 unprotected <Level>k__BackingField: Write Gauge.set_Level | Write Gauge.set_Level"], []),
            ["event"] = (Both("""
                public sealed partial class Gauge { public partial event Action Changed; }
                public sealed partial class Gauge { private int _handlers; public partial event Action Changed { add { _handlers++; } remove { _handlers--; } } }
                public sealed class Holder { private readonly Gauge _gauge = new(); public void Run() => _gauge.Changed += () => { }; }
                """), ["DCA1002 unprotected _handlers: ReadModifyWrite Gauge.add_Changed | ReadModifyWrite Gauge.add_Changed"], [])
        };

    /// <summary>Why a field-backed property's initializer runs its setter.</summary>
    private const string INITIALIZER_CALLS_THE_SETTER =
        "issue 549: the initializer of a property whose accessors use field is lowered as an assignment through the property, a call " +
        "of its setter (none on a get-only one, so the store is lost), while the compiler stores the value into the backing field";

    /// <summary>Why a semicolon accessor beside <c>field</c> is a call without a body.</summary>
    private const string SEMICOLON_ACCESSOR_HAS_NO_BODY =
        "issue 549: a semicolon accessor of a property whose other accessor uses field has the compiler's body over the backing field, " +
        "but ProgramIndexBuilder.HasSourceBody sees no syntax body and IsAutoProperty is false, so its call is an unknown effect";

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why. Each partial
    /// form is paired with the same form without <c>partial</c>, which answers alike: the cause is the <c>field</c> property's.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["initializer-stores-the-backing-field"] =
            new(["DCA1002 unprotected Sets: ReadModifyWrite Gauge.set_Level | ReadModifyWrite Gauge.set_Level"], INITIALIZER_CALLS_THE_SETTER),
        ["plain-initializer-stores-the-backing-field"] =
            new(["DCA1002 unprotected Sets: ReadModifyWrite Gauge.set_Level | ReadModifyWrite Gauge.set_Level"], INITIALIZER_CALLS_THE_SETTER),
        ["get-only-initializer-aliases-a-shared-object"] = new([], INITIALIZER_CALLS_THE_SETTER),
        ["plain-get-only-initializer-aliases-shared-object"] = new([], INITIALIZER_CALLS_THE_SETTER),
        ["semicolon-accessor-beside-field"] =
            new(["DCA1001 unprotected <Level>k__BackingField: UnknownEffect Holder.Run | UnknownEffect Holder.Run"], SEMICOLON_ACCESSOR_HAS_NO_BODY),
        ["plain-semicolon-accessor-beside-field"] =
            new(["DCA1001 unprotected <Level>k__BackingField: UnknownEffect Holder.Run | UnknownEffect Holder.Run"], SEMICOLON_ACCESSOR_HAS_NO_BODY)
    };

    /// <summary>The lost update of a singleton's <c>Value</c> in two runs of its <c>Run</c>.</summary>
    private const string HOLDER_RUN_RACE = "DCA1002 unprotected Value: ReadModifyWrite Holder.Run | ReadModifyWrite Holder.Run";

    /// <summary>Probes whose root or whose binding of the singleton is a partial member, by name: each a whole source with its
    /// startup, and the findings run time calls for.</summary>
    private static readonly Dictionary<string, (string Source, string[] Truth)> RootProbes = new(StringComparer.Ordinal)
    {
        ["hosted-service-with-a-partial-execute-async"] = (Workers("""
            private readonly Holder _holder;
            public {0}(Holder holder) => _holder = holder;
            protected override partial Task ExecuteAsync(CancellationToken stoppingToken);
            """, "protected override partial Task ExecuteAsync(CancellationToken stoppingToken) { _holder.Run(); return Task.CompletedTask; }"),
            [HOLDER_RUN_RACE]),
        ["hosted-service-with-a-partial-constructor"] = (Workers("""
            private readonly Holder _holder;
            public partial {0}(Holder holder);
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { _holder.Run(); return Task.CompletedTask; }
            """, "public partial {0}(Holder holder) => _holder = holder;"), [HOLDER_RUN_RACE]),
        ["controller-with-a-partial-action"] = ("""
            public sealed class Holder { public int Value; public void Run() => Value++; }
            public sealed partial class RunController : ControllerBase
            {
                private readonly Holder _holder;
                public RunController(Holder holder) => _holder = holder;
                [HttpPost("/run")] public partial void Run();
            }
            public sealed partial class RunController { public partial void Run() => _holder.Run(); }
            """ + Startup("services.AddSingleton<Holder>();"), [HOLDER_RUN_RACE]),
        ["minimal-api-with-a-partial-handler"] = ("""
            public sealed class Holder { public int Value; public void Run() => Value++; }
            public static partial class Handlers { public static partial void Run(Holder holder); }
            public static partial class Handlers { public static partial void Run(Holder holder) => holder.Run(); }
            """ + Startup("services.AddSingleton<Holder>(); app.MapPost(\"/run\", Handlers.Run);"), [HOLDER_RUN_RACE])
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The names of the probes whose root or binding is a partial member.</summary>
    public static TheoryData<string> RootNames() => new(RootProbes.Keys);

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

    /// <summary>A probe whose root or binding is a partial member finds what run time calls for, read as sets.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(RootNames))]
    public async Task Root_probe_answers_as_run_time_calls_for(string name)
    {
        var (source, truth) = RootProbes[name];

        var findings = await HostedServiceProbe.FindingsAsync(source);

        HostedServiceProbe.AssertAnswer(findings, truth, null);
    }

    /// <summary>A singleton <c>Holder</c> whose <c>Run</c> increments its value, two hosted services that run it once each, each declared
    /// in two partial parts, and the startup that registers the three; <c>{0}</c> in a part stands for the service's name.</summary>
    /// <param name="defining">The members of each service's first part.</param>
    /// <param name="implementing">The members of each service's second part.</param>
    private static string Workers(string defining, string implementing)
    {
        string Worker(string name) =>
            $"public sealed partial class {name} : BackgroundService\n{{\n{defining.Replace("{0}", name, StringComparison.Ordinal)}\n}}\n" +
            $"public sealed partial class {name} {{ {implementing.Replace("{0}", name, StringComparison.Ordinal)} }}\n";
        return "public sealed class Holder { public int Value; public void Run() => Value++; }\n" + Worker("FirstWorker") + Worker("SecondWorker") +
               Startup("services.AddSingleton<Holder>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();");
    }

    /// <summary>The singleton <c>Holder</c> over a meter, whose <c>Run</c> evaluates <paramref name="expression"/>.</summary>
    /// <param name="expression">The expression <c>Run</c> evaluates.</param>
    private static string Holder(string expression) => HOLDER.Replace("{0}", expression, StringComparison.Ordinal);

    /// <summary>A probe of a singleton <c>Holder</c> whose <c>Run</c> both hosted services call.</summary>
    /// <param name="types">The declaration of <c>Holder</c> and the types it uses.</param>
    private static HostedServiceProbe Both(string types) => new("Holder", types, "_holder.Run();", "_holder.Run();");
}
