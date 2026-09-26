using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The unknown effect of an unresolved call (R1, R2): what it reaches, how it conflicts and is classified, what it leaves alone,
/// and what it publishes.</summary>
public sealed class UnknownEffectTests
{
    private const string TOUCH = "Opaque.Lib.Touch(object)";

    // ---- how it conflicts and is classified ----

    [Fact]
    public async Task Opaque_call_handed_a_singleton_conflicts_with_a_read_of_its_field_in_another_root()
    {
        var finding = Assert.Single(await Findings("Opaque.Lib.Touch(_state);", "_ = _state.Count;"), finding => Path(finding) == "Count");

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal([AccessOperation.Read, AccessOperation.UnknownEffect], new[] { finding.AccessA.Operation, finding.AccessB.Operation }.Order());
    }

    [Fact]
    public async Task Unknown_effect_costs_the_operation_component_alone_and_names_its_gap()
    {
        var written = Assert.Single(await Findings("_state.Count = 1;", "_ = _state.Count;"), finding => Path(finding) == "Count");
        var touched = Assert.Single(await Findings("Opaque.Lib.Touch(_state);", "_ = _state.Count;"), finding => Path(finding) == "Count");

        Assert.Equal(written.Confidence.Components with { Operation = 10 }, touched.Confidence.Components);
        Assert.NotEqual("High", touched.Confidence.Label);
        Assert.Contains(touched.Uncertainty, item => item.Contains(TOUCH, StringComparison.Ordinal) && item.Contains("operation check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_effect_against_a_read_modify_write_is_a_lost_update()
    {
        var finding = Assert.Single(await Findings("Opaque.Lib.Touch(_state);", "_state.Count++;"), finding => Path(finding) == "Count");

        Assert.Equal("DCA1002", finding.RuleId);
    }

    [Fact]
    public async Task Two_unknown_effects_on_one_resource_in_overlapping_executions_conflict()
    {
        var finding = Assert.Single(await Findings("Opaque.Lib.Touch(_state);", "Opaque.Lib.Touch(_state);"), finding => Path(finding) == "Count");

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.True(finding.AccessA.Operation.IsUnknownEffect() && finding.AccessB.Operation.IsUnknownEffect());
    }

    [Fact]
    public void Unknown_effect_and_a_write_under_one_lock_make_no_pair()
    {
        var run = Run("lock (_state.Gate) { Opaque.Lib.Touch(_state); }", "lock (_state.Gate) { _state.Count = 2; }");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Empty(run.PairsOn("Count"));
    }

    [Fact]
    public async Task Unknown_effect_under_a_lock_the_other_side_does_not_hold_is_partial_protection()
    {
        var finding = Assert.Single(await Findings("lock (_state.Gate) { Opaque.Lib.Touch(_state); }", "_state.Count = 2;"),
                                    finding => Path(finding) == "Count");

        Assert.Equal("DCA1003", finding.RuleId);
    }

    // ---- what it reaches ----

    [Fact]
    public void Field_two_levels_down_is_the_resource_an_ordinary_access_names()
    {
        var run = Run("Opaque.Lib.Touch(_state);", "_ = _state.Inner.Leaf.Value;");

        var effect = Assert.Single(run.Of("Value"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region.Contains("Leaf", StringComparison.Ordinal));
        Assert.Contains(run.Of("Value"), access => access.Operation == AccessOperation.Read && access.Resource.Identity == effect.Resource.Identity);
        Assert.NotEmpty(run.PairsOn("Value"));
    }

    [Fact]
    public void Backing_field_of_an_auto_property_is_the_resource_an_ordinary_access_names()
    {
        var run = Run("Opaque.Lib.Touch(_state);", "_ = _state.Auto;");

        var read = Assert.Single(run.Collection.Accesses, access => access.Symbol.StartsWith("Reader.", StringComparison.Ordinal) &&
                                                                    access.Resource.Member.Name.Contains("Auto", StringComparison.Ordinal));
        Assert.Contains(run.Collection.Accesses, access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Identity == read.Resource.Identity);
    }

    [Fact]
    public void Readonly_field_is_only_read_while_its_neighbours_get_the_whole_effect()
    {
        var run = Run("Opaque.Lib.Touch(_state);", "_ = _state.Gate;");

        Assert.Contains(run.Of("Gate"), access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && access.Operation == AccessOperation.Read);
        Assert.DoesNotContain(run.Of("Gate"), access => access.Operation.IsUnknownEffect());
        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Empty(run.PairsOn("Gate"));
    }

    [Fact]
    public void Readonly_field_gets_the_whole_effect_of_a_reflection_call()
    {
        var run = Run("typeof(State).GetField(\"Gate\")!.SetValue(_state, new object());", "_ = _state.Gate;");

        Assert.Contains(run.Of("Gate"), access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && access.Operation == AccessOperation.UnknownEffect);
        Assert.NotEmpty(run.PairsOn("Gate"));
    }

    [Fact]
    public void Field_past_the_summaries_depth_is_a_wildcard()
    {
        var run = Run("Opaque.Lib.Touch(_state.Chain);", "");

        var effects = UnknownEffects(run, "Worker.");
        Assert.Contains(effects, access => access.Resource.IsWildcard && access.Resource.Region.Contains("L8", StringComparison.Ordinal));
        Assert.DoesNotContain(effects, access => !access.Resource.IsWildcard && access.Resource.Region.Contains("L9", StringComparison.Ordinal));
    }

    [Fact]
    public void Cycle_of_references_ends_with_each_field_once()
    {
        var run = Run("Opaque.Lib.Touch(_state.Head);", "");

        Assert.Single(UnknownEffects(run, "Worker."), access => access.Resource.Member.Name == "Value" && access.Resource.Region.Contains("Node", StringComparison.Ordinal));
    }

    [Fact]
    public void List_is_its_structure_and_cells_and_then_the_fields_of_its_elements()
    {
        var run = Run("Opaque.Lib.Touch(_state.Items);", "");

        var effects = UnknownEffects(run, "Worker.");
        Assert.Contains(effects, access => access.Resource.CollectionId is not null && access.Resource.Selector is null && Path(access) == "Items");
        Assert.Contains(effects, access => access.Resource.CollectionId is not null && access.Resource.Selector is not null && Path(access) == "Items.[?]");
        // The element is reached through the cell, and a collection's cells are an escape edge as an array's are (ADR 0010, phase 5b
        // second run): the element of the singleton's list is shared, so the effect on it reads and writes (R1).
        Assert.Contains(run.Of("Value"), access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) &&
                                                   access.Operation == AccessOperation.UnknownEffect &&
                                                   access.Source.StartLine == Line("Opaque.Lib.Touch(_state.Items);"));
    }

    [Fact]
    public void Structure_of_a_ConcurrentDictionary_is_atomic_and_makes_no_pair_with_TryAdd()
    {
        var run = Run("Opaque.Lib.Touch(_state.Map);", "_state.Map.TryAdd(\"a\", 1);");

        Assert.Contains(run.Collection.Accesses, access => access.Operation == AccessOperation.AtomicUnknownEffect && Path(access) == "Map");
        Assert.Empty(run.PairsOn("Map"));
    }

    [Fact]
    public async Task Structure_of_a_ConcurrentDictionary_against_a_count_then_TryAdd_is_a_compound_race()
    {
        var findings = await Findings("Opaque.Lib.Touch(_state.Map);", "if (_state.Map.Count < 10) _state.Map.TryAdd(\"a\", 1);");

        Assert.Contains(findings, finding => finding.RuleId == "DCA1004" && Path(finding) == "Map" &&
                                             (finding.AccessA.Operation.IsUnknownEffect() || finding.AccessB.Operation.IsUnknownEffect()));
    }

    [Fact]
    public async Task Scenario_of_a_compound_race_with_an_unknown_effect_says_what_the_call_may_do()
    {
        var findings = await Findings("Opaque.Lib.Touch(_state.Map);", "if (_state.Map.Count < 10) _state.Map.TryAdd(\"a\", 1);");

        var finding = Assert.Single(findings, finding => finding.RuleId == "DCA1004" && Path(finding) == "Map");
        Assert.Contains(finding.Scenario, step => step.Contains("may read and write `Map` in its call", StringComparison.Ordinal));
    }

    [Fact]
    public void Field_of_an_object_a_ConcurrentDictionary_holds_pairs_with_its_write()
    {
        var run = Run("Opaque.Lib.Touch(_state.Things);", "_state.First.Value = 2;");

        Assert.Contains(run.PairsOn("Value"), pair => pair.First.Operation == AccessOperation.UnknownEffect || pair.Second.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Member_of_the_table_outside_its_version_range_has_an_unknown_effect()
    {
        var run = Run("Newtonsoft.Json.JsonConvert.SerializeObject(_state);", "",
                      extra: [StubAssemblies.Get(StubAssemblies.NEWTONSOFT_JSON, 12)]);

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Array_created_in_the_argument_place_gives_the_effect_to_its_elements()
    {
        var run = Run("Opaque.Lib.Take(new object[] { _state }); Opaque.Lib.Take(_other, 1);", "");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Contains(run.Of("Name"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.DoesNotContain(UnknownEffects(run, "Worker."), access => access.Resource.Region.Contains("Object[]", StringComparison.Ordinal));
    }

    // ---- what the receiver lets it see ----

    [Fact]
    public void Library_members_on_this_of_a_controller_touch_none_of_its_fields_and_do_not_publish_it()
    {
        var run = Run("", "", action: "_hits = 1; _ = HttpContext; return Ok();");

        Assert.Empty(UnknownEffects(run, "PageController."));
        Assert.All(run.Of("_hits"), access => Assert.Equal(OwnershipKind.ThreadConfined, access.Ownership));
    }

    [Fact]
    public void Implicit_base_constructor_of_a_library_type_touches_no_field_of_the_run()
    {
        var run = Run("", "");

        Assert.Empty(UnknownEffects(run, "Worker..ctor"));
        Assert.Empty(UnknownEffects(run, "Reader..ctor"));
        // The constructor ran: its own writes are there, and the library constructor it calls first left no effect beside them.
        Assert.Contains(run.Accesses("_state"), access => access.Symbol.StartsWith("Worker..ctor", StringComparison.Ordinal));
    }

    [Fact]
    public void Base_member_called_on_this_of_a_worker_touches_none_of_its_fields()
    {
        var run = Run("_ = base.StopAsync(stoppingToken);", "");

        Assert.Empty(UnknownEffects(run, "Worker."));
    }

    [Fact]
    public void Library_object_without_content_the_heap_knows_gets_neither_an_access_nor_a_wildcard()
    {
        var run = Run("Opaque.Lib.Touch(_state.Builder);", "");

        Assert.Empty(UnknownEffects(run, "Worker."));
    }

    // ---- the other unresolved calls ----

    [Fact]
    public void Interface_call_without_a_receiver_object_affects_its_arguments()
    {
        var run = Run("_sink!.Put(_state);", "");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Dynamic_call_affects_the_whole_receiver()
    {
        var run = Run("dynamic target = _state; target.Run();", "");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Contains(run.Of("Total"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Dynamic_member_read_affects_the_receiver()
    {
        var run = Run("dynamic target = _state; _ = target.Count;", "");

        Assert.Contains(run.Of("Total"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Dynamic_member_assignment_affects_the_receiver_and_the_assigned_object()
    {
        var run = Run("dynamic target = _state; target.Link = _other;", "");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Contains(run.Of("Name"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Dynamic_compound_assignment_affects_the_receiver()
    {
        var run = Run("dynamic target = _state; target.Total += 2;", "");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Dynamic_increment_affects_the_receiver()
    {
        var run = Run("dynamic target = _state; target.Count++;", "");

        Assert.Contains(run.Of("Total"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Dynamic_indexer_read_and_write_affect_the_receiver_and_the_assigned_object()
    {
        var run = Run("dynamic target = _state; var first = target[0]; target[1] = _other;", "");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Contains(run.Of("Name"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    // ---- guards ----

    [Fact]
    public void Unknown_effect_under_a_flag_and_a_write_under_its_negation_make_no_pair()
    {
        var run = Run("if (_options.IsPrimary) Opaque.Lib.Touch(_state);", "if (!_options.IsPrimary) _state.Count = 2;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Empty(run.PairsOn("Count"));
        Assert.True(run.Skipped(InterproceduralPairing.SKIP_UNSATISFIABLE_PATH) > 0);
    }

    [Fact]
    public void Guard_the_caller_puts_on_the_call_of_a_helper_that_passes_its_argument_on_excludes_the_pair_too()
    {
        var run = Run("if (_options.IsPrimary) Hand(_state);", "if (!_options.IsPrimary) _state.Count = 2;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Empty(run.PairsOn("Count"));
        Assert.True(run.Skipped(InterproceduralPairing.SKIP_UNSATISFIABLE_PATH) > 0);
    }

    // ---- objects of one execution and publication ----

    [Fact]
    public void Fresh_object_handed_to_the_call_stays_confined_and_is_only_read()
    {
        var run = Run("", "", action: "var local = new Item(); local.Value = 1; Opaque.Lib.Touch(local); return Ok();");

        var accesses = run.Of("Value").Where(access => access.Symbol.StartsWith("PageController.", StringComparison.Ordinal)).ToArray();
        Assert.Contains(accesses, access => access.Operation == AccessOperation.Read);
        Assert.DoesNotContain(accesses, access => access.Operation.IsUnknownEffect());
        Assert.All(accesses, access => Assert.Equal(OwnershipKind.ThreadConfined, access.Ownership));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Object_of_one_execution_holding_a_singleton_hands_the_singleton_the_whole_effect()
    {
        var run = Run("var wrapper = new Wrapper(); wrapper.Mark = 1; wrapper.State = _state; Opaque.Lib.Touch(wrapper);", "_state.Count = 2;");

        Assert.Contains(run.PairsOn("Count"), pair => pair.First.Operation == AccessOperation.UnknownEffect || pair.Second.Operation == AccessOperation.UnknownEffect);
        Assert.Contains(run.Of("Mark"), access => access.Operation == AccessOperation.Read && access.Ownership == OwnershipKind.ThreadConfined);
        Assert.DoesNotContain(run.Of("Mark"), access => access.Operation.IsUnknownEffect());
    }

    [Fact]
    public void Constructor_of_a_singleton_passing_this_to_the_call_publishes_it()
    {
        var run = Run("", "", action: "_ = _thing.Status; return Ok();");

        Assert.False(Assert.Single(Writes(run, "Thing..ctor()", "Status")).IsConstructionLocal);
        Assert.NotEmpty(run.PairsOn("Status"));
    }

    [Fact]
    public void Constructor_of_an_object_of_one_execution_passing_this_to_the_call_does_not_publish_it()
    {
        var run = Run("", "", action: "var draft = new Draft(); GC.KeepAlive(draft); return Ok();");

        Assert.NotEmpty(Writes(run, "Draft..ctor()", "Body"));
        Assert.All(Writes(run, "Draft..ctor()", "Body"), access => Assert.True(access.IsConstructionLocal));
    }

    [Fact]
    public void Known_call_neither_publishes_nor_has_an_unknown_effect()
    {
        var run = Run("", "", action: "_ = _known.Status; return Ok();");

        Assert.True(Assert.Single(Writes(run, "Known..ctor()", "Status")).IsConstructionLocal);
        Assert.Empty(UnknownEffects(run, "Known."));
    }

    [Fact]
    public void GC_KeepAlive_leaves_the_object_confined()
    {
        var run = Run("", "", action: "var local = new Item(); local.Value = 1; GC.KeepAlive(local); return Ok();");

        Assert.All(run.Of("Value").Where(access => access.Symbol.StartsWith("PageController.", StringComparison.Ordinal)),
                   access => Assert.Equal(OwnershipKind.ThreadConfined, access.Ownership));
        Assert.Empty(UnknownEffects(run, "PageController."));
    }

    // ---- the calls a recognizer models ----

    [Theory]
    [InlineData("var taken = false; _state.Spin.Enter(ref taken);")]
    [InlineData("var span = System.MemoryExtensions.AsSpan(_state.Letters); span[0] = 'a';")]
    [InlineData("_state.Signal.Set();")]
    [InlineData("_state.Named = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(new NameComparer());")]
    [InlineData("foreach (var letter in _state.Letters) { }")]
    public void Call_a_recognizer_models_has_no_unknown_effect(string work)
    {
        var run = Run(work, "");

        Assert.Empty(UnknownEffects(run, "Worker."));
    }

    [Fact]
    public void Comparer_handed_to_a_ConcurrentDictionary_constructor_is_not_published()
    {
        var run = Run("_state.Named = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(new NameComparer());", "");

        Assert.All(run.Of("Calls"), access => Assert.NotEqual(OwnershipKind.Escaped, access.Ownership));
    }

    // ---- where it stands ----

    [Fact]
    public void Place_and_symbol_are_the_call_site_and_its_member()
    {
        var run = Run("Opaque.Lib.Touch(_state);", "");

        var effect = Assert.Single(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Equal("Worker.ExecuteAsync(CancellationToken)", effect.Symbol);
        Assert.Equal(Line("Opaque.Lib.Touch(_state);"), effect.Source.StartLine);
    }

    [Fact]
    public void Result_of_an_opaque_call_points_to_nothing()
    {
        var made = Run("_state.Link = Opaque.Lib.Make(); ((Item)_state.Link).Value = 2;", "");
        var created = Run("_state.Link = new Item(); ((Item)_state.Link).Value = 2;", "");

        Assert.DoesNotContain(made.Of("Value"), access => access.Operation == AccessOperation.Write);
        Assert.Contains(created.Of("Value"), access => access.Operation == AccessOperation.Write);
    }

    // ---- helpers ----

    private static string Path(Finding finding) => string.Join(".", finding.Resource.AccessPath);

    private static string Path(Access access) => string.Join(".", access.Resource.AccessPath);

    private static Access[] UnknownEffects(EngineRun run) => run.Collection.Accesses.Where(access => access.Operation.IsUnknownEffect()).ToArray();

    /// <summary>The unknown effects of the members whose symbol starts with <paramref name="symbol"/>: the fixture's own constructors
    /// make unresolved calls of their own.</summary>
    private static Access[] UnknownEffects(EngineRun run, string symbol) =>
        UnknownEffects(run).Where(access => access.Symbol.StartsWith(symbol, StringComparison.Ordinal)).ToArray();

    private static Access[] Writes(EngineRun run, string symbol, string field) =>
        run.Collection.Accesses.Where(access => access.Symbol == symbol && Path(access) == field && access.Operation == AccessOperation.Write).ToArray();

    private static int Line(string work) =>
        Source(work, "", DEFAULT_ACTION).Split('\n').Select((line, index) => (line, index)).First(pair => pair.line.Contains(work, StringComparison.Ordinal)).index + 1;

    private const string DEFAULT_ACTION = "return Ok();";

    private static EngineRun Run(string work, string other, string action = DEFAULT_ACTION, IReadOnlyList<MetadataReference>? extra = null) =>
        AnalyzeScope(FixtureSolution.Create(Options(extra), ("Case.cs", Source(work, other, action))), "scope:Fixture");

    private static async Task<IReadOnlyList<Finding>> Findings(string work, string other) =>
        (await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(Options(null), ("Case.cs", Source(work, other, DEFAULT_ACTION))), ROOT_DIRECTORY,
                                             CancellationToken.None)).Findings;

    private static FixtureOptions Options(IReadOnlyList<MetadataReference>? extra) =>
        new() { MetadataReferences = [OpaqueLibrary.Value, .. extra ?? []] };

    /// <summary>A singleton <c>State</c> and its neighbours: one worker does <paramref name="work"/>, another does <paramref name="other"/>,
    /// and a controller's action does <paramref name="action"/>.</summary>
    private static string Source(string work, string other, string action) => Usings + $$"""
        using System.Collections.Generic;
        using System.Collections.Concurrent;

        public sealed class Item { public int Value; }
        public sealed class Other { public string Name = ""; }
        public sealed class Leaf { public int Value; }
        public sealed class Middle { public Leaf Leaf = new(); }
        public sealed class Node { public Node? Next; public int Value; }
        public sealed class Wrapper { public int Mark; public State? State; }
        public sealed class L9 { public int V; }
        public sealed class L8 { public L9 Next = new(); public int V; }
        public sealed class L7 { public L8 Next = new(); public int V; }
        public sealed class L6 { public L7 Next = new(); public int V; }
        public sealed class L5 { public L6 Next = new(); public int V; }
        public sealed class L4 { public L5 Next = new(); public int V; }
        public sealed class L3 { public L4 Next = new(); public int V; }
        public sealed class L2 { public L3 Next = new(); public int V; }
        public sealed class L1 { public L2 Next = new(); public int V; }
        public sealed class L0 { public L1 Next = new(); public int V; }

        public sealed class Options { public bool IsPrimary { get; } = true; }

        public sealed class NameComparer : IEqualityComparer<string>
        {
            public int Calls;
            public bool Equals(string? x, string? y) => x == y;
            public int GetHashCode(string value) => value.Length;
        }

        public sealed class State
        {
            public int Count;
            public int Total;
            public int Auto { get; set; }
            public Middle Inner = new();
            public L0 Chain = new();
            public Node Head = new();
            public Item First = new();
            public List<Item> Items = new();
            public ConcurrentDictionary<string, int> Map = new();
            public ConcurrentDictionary<string, Item> Things = new();
            public ConcurrentDictionary<string, int>? Named;
            public readonly object Gate = new();
            public char[] Letters = new char[2];
            public SpinLock Spin = new(false);
            public readonly ManualResetEventSlim Signal = new();
            public readonly System.Text.StringBuilder Builder = new();
            public object? Link;

            public State()
            {
                Head.Next = Head;
                Items.Add(new Item());
                Things.TryAdd("a", First);
            }
        }

        public sealed class Thing { public object? Status; public Thing() { Opaque.Lib.Touch(this); Status = new object(); } }
        public sealed class Draft { public string Body = ""; public Draft() { Opaque.Lib.Touch(this); Body = "draft"; } }
        public sealed class Known { public object? Status; public Known() { System.Text.Json.JsonSerializer.Serialize(this); Status = new object(); } }

        public interface ISink { void Put(State state); }
        public sealed class Sink : ISink { public int Puts; public void Put(State state) => Puts++; }

        public sealed class Worker : BackgroundService
        {
            private readonly State _state;
            private readonly Other _other;
            private readonly Options _options;
            private readonly ISink? _sink = null;
            public Worker(State state, Other other, Options options)
            {
                _state = state;
                _other = other;
                _options = options;
            }

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }

            private static void Hand(State state) => Opaque.Lib.Touch(state);
        }

        public sealed class Reader : BackgroundService
        {
            private readonly State _state;
            private readonly Options _options;
            public Reader(State state, Options options)
            {
                _state = state;
                _options = options;
            }

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{other}}
                return Task.CompletedTask;
            }
        }

        public sealed class PageController : ControllerBase
        {
            private readonly Thing _thing;
            private readonly Known _known;
            private int _hits;
            public PageController(Thing thing, Known known)
            {
                _thing = thing;
                _known = known;
            }

            public IActionResult Get()
            {
                {{action}}
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Other>(); services.AddSingleton<Options>(); " +
                      "services.AddSingleton<Thing>(); services.AddSingleton<Known>(); " +
                      "services.AddHostedService<Worker>(); services.AddHostedService<Reader>();");

    /// <summary>A library the run has no source of: every member is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib
            {
                public static void Touch(object value) { }
                public static void Take(params object[] values) { }
                public static object Make() => new();
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
