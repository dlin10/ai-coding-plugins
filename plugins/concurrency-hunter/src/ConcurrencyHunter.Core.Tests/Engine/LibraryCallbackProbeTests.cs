using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of code a library runs or state a library hands out (issue 506), each with what run time calls for.
/// <c>Progress&lt;T&gt;</c> with no synchronization context runs its handler on the thread pool, possibly "concurrently with itself"
/// (<c>Progress&lt;T&gt;</c> constructor). <c>IOptions&lt;T&gt;</c> registered from one <c>Options.Create</c> value is that one object
/// for every consumer. Replacing an immutable list with <c>_list = _list.Add(x)</c> is a read and a write that depends on it;
/// <c>ImmutableInterlocked.Update</c> retries a compare-and-swap, and its transformer "may run multiple times when races occur with
/// other threads"; making a frozen dictionary reads the source dictionary, which another service writes meanwhile. PLINQ runs its
/// delegates on several threads at once. The options types come from a stub assembly with the framework's name
/// (<see cref="StubAssemblies.OPTIONS"/>), so the framework census applies to them. Every probe runs through the whole analysis
/// (<see cref="HostedServiceProbe"/>) and names each finding's pair of sites; a probe the analysis answers otherwise is named in
/// <see cref="Differences"/> with its answer and why.</summary>
public sealed class LibraryCallbackProbeTests
{
    /// <summary>Why a delegate a library runs on several threads does not pair with itself.</summary>
    private const string ONE_HAND_OFF_DOES_NOT_OVERLAP_ITSELF =
        "limit: no model describes the member, so the delegate runs in an unknown execution (ADR 0011), and one a run-once execution " +
        "hands over at one site does not overlap itself, though this library runs it on several threads at once";

    /// <summary>Why the two services' <c>Add</c> to the options' list pair as unknown effects.</summary>
    private const string OPTIONS_VALUE_IS_A_GAP =
        "limit: Options.Create has a census model that keeps its argument, but IOptions<T>.get_Value has none (its generation failed), " +
        "so the shared Settings is reached through a gap: the race is reported, as an unknown effect on the list with an uncertainty, " +
        "not as the Add's writes of its structure and cells";

    /// <summary>Why making a frozen dictionary pairs as an unknown effect.</summary>
    private const string FROZEN_DICTIONARY_IS_A_GAP =
        "limit: ToFrozenDictionary has no model, so its reading of the source is an unknown effect, which the analysis counts as a " +
        "possible write; the pair with Set is right, its operation is not";

    /// <summary>The structure and cell pairs of two <c>Add</c> calls on <paramref name="path"/>, one at each side.</summary>
    /// <param name="path">The list's access path.</param>
    /// <param name="first">The site of one <c>Add</c>.</param>
    /// <param name="second">The site of the other.</param>
    private static string[] TwoAdds(string path, string first, string second)
    {
        var sides = string.Join(" | ", new[] { $"Write {first}", $"Write {second}" }.Order(StringComparer.Ordinal));
        return [$"DCA1001 unprotected {path} structure: {sides}", $"DCA1001 unprotected {path}.[?]: {sides}"];
    }

    /// <summary>Each probe by name, with the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes them, and
    /// what its project holds besides its source.</summary>
    private static readonly Dictionary<string, (HostedServiceProbe Probe, string[] Truth, ProbeInputs Inputs)> Probes = new(StringComparer.Ordinal)
    {
        ["progress-handler-reported-twice"] = (Run("Log", "private readonly List<int> _entries = new();",
            "public void Run() { IProgress<int> p = new Progress<int>(v => /*@handler*/_entries.Add(v)); p.Report(1); p.Report(2); }"),
            TwoAdds("_entries", "Log.Run @handler", "Log.Run @handler"), new()),
        ["progress-handler-against-its-reporter"] = (Run("Log", "private readonly List<int> _entries = new();",
            "public void Run() { IProgress<int> p = new Progress<int>(v => /*@handler*/_entries.Add(v)); p.Report(1); /*@reporter*/_entries.Add(0); }"),
            TwoAdds("_entries", "Log.Run @handler", "Log.Run @reporter"), new()),
        ["options-value-list-added-to-by-two-services"] = (new("Registrar", """
            public sealed class Settings { public List<string> Endpoints = new(); }
            public sealed class Registrar(Microsoft.Extensions.Options.IOptions<Settings> options) { public void Add(string url) => options.Value.Endpoints.Add(url); }
            """, "_registrar.Add(\"a\");", "_registrar.Add(\"b\");", "services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new Settings()));"),
            TwoAdds("Endpoints", "Registrar.Add", "Registrar.Add"), new([], [StubAssemblies.OPTIONS], true)),
        ["immutable-list-replaced-by-its-successor"] = (Registry("private ImmutableList<int> _list = ImmutableList<int>.Empty;",
            "public void Add(int x) => _list = _list.Add(x);"), ["DCA1002 unprotected _list: ReadModifyWrite Registry.Add | ReadModifyWrite Registry.Add"], new()),
        ["immutable-interlocked-update"] = (Registry("private ImmutableList<int> _list = ImmutableList<int>.Empty;",
            "public void Add(int x) => ImmutableInterlocked.Update(ref _list, l => l.Add(x));"), [], new()),
        ["immutable-interlocked-update-whose-transformer-counts"] = (Registry(
            "private int _calls; private ImmutableList<int> _list = ImmutableList<int>.Empty;",
            "public void Add(int x) => ImmutableInterlocked.Update(ref _list, l => { _calls++; return l.Add(x); });"),
            ["DCA1002 unprotected _calls: ReadModifyWrite Registry.Add | ReadModifyWrite Registry.Add"], new()),
        ["frozen-dictionary-made-from-a-dictionary-being-written"] = (new("Registry", """
            public sealed class Registry
            {
                private readonly Dictionary<string, int> _builder = new();
                private System.Collections.Frozen.FrozenDictionary<string, int>? _routes;
                public void Set() => _builder["a"] = 1;
                public void Freeze() => _routes = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(_builder);
            }
            """, "_registry.Set();", "_registry.Freeze();"),
            ["DCA1001 unprotected _builder structure: Read Registry.Freeze | Write Registry.Set",
             "DCA1001 unprotected _builder.[\"a\"]: Read Registry.Freeze | Write Registry.Set"], new()),
        ["plinq-for-all-writes-a-field"] = (Run("Sink", "private int _last;",
            "public void Run() { new[] { 1, 2, 3 }.AsParallel().ForAll(x => _last = x); }"), ["DCA1001 unprotected _last: Write Sink.Run | Write Sink.Run"], new()),
        ["plinq-where-adds-to-a-set"] = (Run("Sink", "private readonly HashSet<int> _seen = new();",
            "public void Run() { _ = new[] { 1, 2, 3 }.AsParallel().Where(x => _seen.Add(x)).ToList(); }"), TwoAdds("_seen", "Sink.Run", "Sink.Run"), new()),
        ["linq-where-adds-to-a-set"] = (Run("Sink", "private readonly HashSet<int> _seen = new();",
            "public void Run() { _ = new[] { 1, 2, 3 }.Where(x => _seen.Add(x)).ToList(); }"), [], new())
    };

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["progress-handler-reported-twice"] = new([], ONE_HAND_OFF_DOES_NOT_OVERLAP_ITSELF),
        ["options-value-list-added-to-by-two-services"] = new(["DCA1001 unprotected Endpoints: UnknownEffect Registrar.Add | UnknownEffect Registrar.Add (uncertain)",
                                                               "DCA1001 unprotected Endpoints.[?]: UnknownEffect Registrar.Add | UnknownEffect Registrar.Add (uncertain)"],
                                                              OPTIONS_VALUE_IS_A_GAP),
        ["frozen-dictionary-made-from-a-dictionary-being-written"] = new(["DCA1001 unprotected _builder structure: UnknownEffect Registry.Freeze | Write Registry.Set",
                                                                          "DCA1001 unprotected _builder.[\"a\"]: UnknownEffect Registry.Freeze | Write Registry.Set"],
                                                                         FROZEN_DICTIONARY_IS_A_GAP),
        ["plinq-for-all-writes-a-field"] = new([], ONE_HAND_OFF_DOES_NOT_OVERLAP_ITSELF),
        ["plinq-where-adds-to-a-set"] = new([], ONE_HAND_OFF_DOES_NOT_OVERLAP_ITSELF)
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The probe's findings are what run time calls for, or the answer its difference pins, read as sets.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Probe_answers_as_run_time_calls_for_or_as_its_named_difference(string name)
    {
        var (probe, truth, inputs) = Probes[name];

        var answer = await probe.AnalyzeAsync(inputs);

        HostedServiceProbe.AssertAnswer(answer.Findings, truth, Differences.GetValueOrDefault(name));
    }

    /// <summary>The uncertainty the options-value findings carry names the unresolved call of <c>IOptions&lt;T&gt;.get_Value</c>, the gap
    /// its difference gives as the reason.</summary>
    [Fact]
    public async Task Options_value_uncertainty_names_the_unresolved_get_Value()
    {
        var (probe, _, inputs) = Probes["options-value-list-added-to-by-two-services"];

        var answer = await probe.AnalyzeAsync(inputs);

        Assert.Contains(answer.Uncertainties, line => line.Contains("IOptions<Settings>.get_Value", StringComparison.Ordinal));
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);

    /// <summary>A probe of a service whose <c>Run</c> the first hosted service calls; the second does nothing.</summary>
    /// <param name="service">The service's name.</param>
    /// <param name="field">The service's field.</param>
    /// <param name="run">The declaration of <c>Run</c>.</param>
    private static HostedServiceProbe Run(string service, string field, string run) => new(service, $$"""
        public sealed class {{service}}
        {
            {{field}}
            {{run}}
        }
        """, $"_{char.ToLowerInvariant(service[0])}{service[1..]}.Run();", "_ = 0;");

    /// <summary>A probe of a <c>Registry</c> over <c>System.Collections.Immutable</c> whose <c>Add</c> both hosted services call.</summary>
    /// <param name="field">The registry's fields.</param>
    /// <param name="add">The declaration of <c>Add</c>.</param>
    private static HostedServiceProbe Registry(string field, string add) => new("Registry", $$"""
        public sealed class Registry
        {
            {{field}}
            {{add}}
        }
        """.Replace("ImmutableList", "System.Collections.Immutable.ImmutableList", StringComparison.Ordinal)
           .Replace("ImmutableInterlocked", "System.Collections.Immutable.ImmutableInterlocked", StringComparison.Ordinal),
        "_registry.Add(1);", "_registry.Add(2);");
}
