using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class IteratorExecutionTests
{
    [Fact]
    public void Iterator_body_runs_at_enumeration_not_creation()
    {
        var program = Solve(Usings + """
            using System.Collections.Generic;
            public sealed class State
            {
                public int Value;
                public IEnumerable<int> Walk() { Value = 1; yield return 1; }
            }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    foreach (var item in _state.Walk()) _state.Value = item;
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"));
        var creation = Assert.Single(program.Heap.IteratorObjects).Creation;
        Assert.DoesNotContain(program.Heap.ExecutionEdges, edge => edge == creation);
        Assert.Contains(program.Heap.ExecutionEdges, edge => edge.CalleeInstance == creation.CalleeInstance &&
                                                              edge.OperationId != creation.OperationId &&
                                                              edge.Reason == "iterator-enumeration");
    }

    [Fact]
    public void Iterator_created_under_lock_is_unprotected_when_enumerated_outside()
    {
        var run = Analyze(Case("lock (_state.Gate) { sequence = _state.Walk(); } foreach (var value in sequence) { }",
                               "System.Collections.Generic.IEnumerable<int> sequence;",
                               "lock (_state.Gate) { _state.Value = 2; }"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
        Assert.Empty(pair.First.HeldProtection);
    }

    [Fact]
    public void Iterator_enumerated_under_lock_is_protected()
    {
        var run = Analyze(Case("var sequence = _state.Walk(); lock (_state.Gate) { foreach (var value in sequence) { } }",
                               "", "lock (_state.Gate) { _state.Value = 2; }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Monitor_held_at_yield_partially_protects_foreach_body()
    {
        var run = Analyze(Case("foreach (var value in _state.Walk()) { _state.Value = value; }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "Monitor.Enter(Gate); try { yield return 1; } finally { Monitor.Exit(Gate); }"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
        Assert.NotEmpty(pair.First.HeldProtection);
        Assert.All(pair.First.HeldProtections.Values.SelectMany(held => held), held => Assert.False(held.IsExclusive));
    }

    [Fact]
    public void Semaphore_held_at_yield_protects_foreach_body()
    {
        var run = Analyze(Case("foreach (var value in _state.Walk()) { _state.Value = value; }", "",
                               "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }",
                               "One.Wait(); try { yield return 1; } finally { One.Release(); }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Iterator_escaped_to_field_runs_in_unknown_execution()
    {
        var run = Analyze(Case("_state.Escaped = _state.Walk();", "", "_state.Value = 2;"));
        var unknown = Assert.Single(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownEnumeration);
        Assert.Contains("unknown enumeration", unknown.Display, StringComparison.Ordinal);
        Assert.Contains(run.Accesses("Value"), access => access.ExecutionId == unknown.Id);
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId == unknown.Id || pair.Second.ExecutionId == unknown.Id);
    }

    [Fact]
    public void Unknown_enumeration_overlaps_itself()
    {
        var run = Analyze(Case("_state.Escaped = _state.Walk();", "", ""));
        var unknown = Assert.Single(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownEnumeration);
        Assert.True(run.Execution.Analysis.Overlaps(unknown.Id, unknown.Id));
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId == unknown.Id && pair.Second.ExecutionId == unknown.Id);
    }

    [Fact]
    public void Escaped_iterator_does_not_keep_creation_lock()
    {
        var run = Analyze(Case("lock (_state.Gate) { _state.Escaped = _state.Walk(); }", "",
                               "lock (_state.Gate) { _state.Value = 2; }"));
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.ExecutionId.Contains("unknown-enumeration", StringComparison.Ordinal) ||
                                                        pair.Second.ExecutionId.Contains("unknown-enumeration", StringComparison.Ordinal));
    }

    [Fact]
    public void Opaque_consumer_adds_unknown_enumeration()
    {
        var run = Analyze(Case("System.Linq.Enumerable.ToList(_state.Walk());", "", "_state.Value = 2;"));
        Assert.Contains(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownEnumeration);
    }

    [Fact]
    public void Explicit_GetEnumerator_adds_unknown_enumeration()
    {
        var run = Analyze(Case("var iterator = _state.Walk().GetEnumerator(); iterator.MoveNext();", "", "_state.Value = 2;"));
        Assert.Contains(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownEnumeration);
    }

    [Fact]
    public void Iterator_finally_releases_lock_before_code_after_loop()
    {
        var run = Analyze(Case("foreach (var value in _state.Walk()) { } _state.Value = 1;", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "Monitor.Enter(Gate); try { yield return 1; } finally { Monitor.Exit(Gate); }"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
        Assert.Empty(pair.First.HeldProtection);
    }

    [Fact]
    public void Only_one_yield_under_lock_does_not_protect_loop_body()
    {
        var run = Analyze(Case("foreach (var value in _state.Walk()) { _state.Value = value; }", "",
                               "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }",
                               "if (DateTime.UtcNow.Ticks > 0) { yield return 2; } else { One.Wait(); try { yield return 1; } finally { One.Release(); } }"));
        Assert.Equal(PairProtection.PARTIAL, Assert.Single(run.PairsOn("Value")).Protection);
    }

    [Fact]
    public void Iterator_or_array_does_not_protect_loop_body()
    {
        var run = Analyze(Case("IEnumerable<int> sequence = DateTime.UtcNow.Ticks > 0 ? _state.Walk() : new[] { 1 }; " +
                               "foreach (var value in sequence) { _state.Value = value; }", "",
                               "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }",
                               "One.Wait(); try { yield return 1; } finally { One.Release(); }"));
        Assert.Equal(PairProtection.PARTIAL, Assert.Single(run.PairsOn("Value")).Protection);
    }

    [Fact]
    public void One_of_two_iterators_without_lock_does_not_protect_loop_body()
    {
        var run = Analyze(Case("IEnumerable<int> sequence = DateTime.UtcNow.Ticks > 0 ? _state.Walk() : _state.Other(); " +
                               "foreach (var value in sequence) { _state.Value = value; }", "",
                               "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }",
                               "One.Wait(); try { yield return 1; } finally { One.Release(); }",
                               "public IEnumerable<int> Other() { yield return 2; }"));
        Assert.Equal(PairProtection.PARTIAL, Assert.Single(run.PairsOn("Value")).Protection);
    }

    [Fact]
    public void Iterator_passed_to_another_method_runs_at_its_foreach()
    {
        var run = Analyze(Case("Consume(_state.Walk()); void Consume(IEnumerable<int> items) { " +
                               "lock (_state.Gate) { foreach (var item in items) { } } }", "",
                               "lock (_state.Gate) { _state.Value = 2; }"));
        Assert.Empty(run.PairsOn("Value"));
        Assert.Contains(run.Accesses("Value"), access => access.Symbol.Contains("Walk", StringComparison.Ordinal) &&
                                                       access.HeldProtection.Count != 0);
    }

    [Fact]
    public void Iterator_finally_releases_lock_after_break()
    {
        var run = Analyze(Case("foreach (var value in _state.Walk()) { break; } _state.Value = 1;", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "Monitor.Enter(Gate); try { yield return 1; } finally { Monitor.Exit(Gate); }"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
        Assert.Empty(pair.First.HeldProtection);
    }

    [Fact]
    public void Iterator_finally_releases_lock_after_iterator_exception()
    {
        var run = Analyze(Case("try { foreach (var value in _state.Walk()) { } } catch (Exception) { _state.Value = 1; }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "Monitor.Enter(Gate); try { yield return 1; throw new Exception(); } finally { Monitor.Exit(Gate); }"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
        Assert.Empty(pair.First.HeldProtection);
    }

    [Fact]
    public void Iterator_finally_releases_lock_after_loop_body_exception()
    {
        var run = Analyze(Case("try { foreach (var value in _state.Walk()) { throw new Exception(); } } " +
                               "catch (Exception) { _state.Value = 1; }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "Monitor.Enter(Gate); try { yield return 1; } finally { Monitor.Exit(Gate); }"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
        Assert.Empty(pair.First.HeldProtection);
    }

    [Fact]
    public void Iterator_or_array_leaves_no_lock_after_loop()
    {
        var run = Analyze(Case("IEnumerable<int> sequence = DateTime.UtcNow.Ticks > 0 ? _state.Walk() : new[] { 1 }; " +
                               "try { foreach (var value in sequence) { } _state.Value = 1; } finally { _state.One.Release(); }", "",
                               "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }",
                               "One.Wait(); yield return 1;"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Equal(PairProtection.PARTIAL, pair.Protection);
        Assert.Empty(pair.First.HeldProtection);
    }

    [Fact]
    public void Iterator_lock_left_open_is_held_after_normal_exhaustion()
    {
        var run = Analyze(Case("try { foreach (var value in _state.Walk()) { } _state.Value = 1; } " +
                               "finally { _state.One.Release(); }", "",
                               "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }",
                               "One.Wait(); yield return 1;"));
        Assert.NotEmpty(Assert.Single(run.PairsOn("Value")).First.HeldProtection);
    }

    [Fact]
    public void Iterator_lock_left_open_is_held_after_break()
    {
        var run = Analyze(Case("try { foreach (var value in _state.Walk()) { break; } _state.Value = 1; } " +
                               "finally { _state.One.Release(); }", "",
                               "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }",
                               "One.Wait(); yield return 1;"));
        Assert.NotEmpty(Assert.Single(run.PairsOn("Value")).First.HeldProtection);
    }

    [Fact]
    public void Iterator_finally_leaving_the_callers_monitor_leaves_nothing_held_after_loop()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); foreach (var value in _state.Walk()) { } _state.Value = 1;", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "try { yield return 1; } finally { Monitor.Exit(Gate); }"));
        Assert.Empty(Assert.Single(run.PairsOn("Value")).First.HeldProtection);
    }

    [Fact]
    public void Iterator_leaving_the_callers_monitor_before_its_yield_leaves_the_loop_body_unprotected()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); foreach (var value in _state.Walk()) { _state.Value = value; }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "Monitor.Exit(Gate); yield return 1;"));
        Assert.Empty(Assert.Single(run.PairsOn("Value")).First.HeldProtection);
    }

    [Fact]
    public void Iterator_taking_the_callers_monitor_again_keeps_the_callers_holding()
    {
        var run = Analyze(Case("lock (_state.Gate) { foreach (var value in _state.Walk()) { } _state.Value = 1; }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "lock (Gate) { yield return 1; }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Iterator_that_may_leave_the_callers_monitor_before_its_yield_leaves_the_loop_body_unprotected()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); foreach (var value in _state.Walk()) { _state.Value = value; }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "if (DateTime.UtcNow.Ticks > 0) Monitor.Exit(Gate); yield return 1;"));
        Assert.Empty(Assert.Single(run.PairsOn("Value")).First.HeldProtection);
    }

    [Fact]
    public void Iterator_that_may_leave_the_callers_monitor_only_at_its_end_keeps_the_loop_body_held()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); foreach (var value in _state.Walk()) { _state.Value = value; }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "try { yield return 1; } finally { if (DateTime.UtcNow.Ticks > 0) Monitor.Exit(Gate); }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Iterator_that_may_leave_the_callers_monitor_at_its_end_leaves_the_code_after_the_loop_unprotected()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); foreach (var value in _state.Walk()) { } _state.Value = 1;", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "try { yield return 1; } finally { if (DateTime.UtcNow.Ticks > 0) Monitor.Exit(Gate); }"));
        Assert.Empty(Assert.Single(run.PairsOn("Value")).First.HeldProtection);
    }

    [Fact]
    public void Async_iterator_keeps_its_existing_model()
    {
        var program = Reach(Usings + """
            using System.Collections.Generic;
            public sealed class State
            {
                public int Value;
                public async IAsyncEnumerable<int> WalkAsync()
                {
                    await Task.Yield();
                    Value = 1;
                    yield return 1;
                }
            }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var items = _state.WalkAsync();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"));
        var body = Assert.Single(program.Result.Bodies.Values, body => body.MethodSymbol.Contains("WalkAsync", StringComparison.Ordinal));
        Assert.True(body.IsAsyncIterator);
        Assert.False(body.IsIterator);
    }

    [Fact]
    public async Task Unknown_enumeration_of_shared_project_stays_inside_each_process_scope()
    {
        var app = Usings + "using Shared;\nSystem.Console.WriteLine();\n" + """
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _state.Escaped = _state.Walk();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();");
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind>
                {
                    ["AppA"] = OutputKind.ConsoleApplication,
                    ["AppB"] = OutputKind.ConsoleApplication
                },
                ProjectReferences = [("AppA", "Shared"), ("AppB", "Shared")]
            },
            ("Shared", "State.cs", """
                using System.Collections.Generic;
                namespace Shared;
                public sealed class State
                {
                    public IEnumerable<int>? Escaped;
                    public int Value;
                    public IEnumerable<int> Walk() { Value = 1; yield return 1; }
                }
                """),
            ("AppA", "Program.cs", app),
            ("AppB", "Program.cs", app));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, CancellationToken.None);
        var findings = result.Findings.Where(finding => finding.Resource.AccessPath.SequenceEqual(["Value"])).ToArray();
        Assert.Equal(2, findings.Length);
        Assert.All(findings, finding =>
        {
            Assert.Equal(finding.AccessA.Root.Scope, finding.AccessB.Root.Scope);
            Assert.Equal("unknown-enumeration", finding.AccessA.Root.RootKind);
        });
        Assert.Equal(2, findings.Select(finding => finding.Resource.Scope).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Iterator_arguments_bound_at_creation_name_exact_cells_in_its_body()
    {
        var run = Analyze(Case("foreach (var value in _state.WalkAt(3)) { }", "", "foreach (var value in _state.WalkAt(4)) { }",
                               extraState: "public int[] Slots = new int[8]; " +
                                           "public IEnumerable<int> WalkAt(int index) { Slots[index] = 1; yield return index; }"));
        Assert.Equal(["[3]", "[4]"], run.Collection.Accesses.Where(access => access.Resource.AccessPath.Contains("Slots") &&
                                                                             access.Resource.Selector is not null)
                                                            .Select(access => access.Resource.Selector!.Text).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.Resource.AccessPath.Contains("Slots"));
    }

    [Fact]
    public void Enumerators_lock_an_iterator_lets_go_only_in_its_finally_protects_the_loop_body()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); foreach (var value in _state.Walk()) { _state.Value = value; }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "try { yield return 1; } finally { Monitor.Exit(Gate); }"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Enumerators_lock_an_iterator_lets_go_in_its_finally_is_gone_after_the_loop()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); foreach (var value in _state.Walk()) { } _state.Value = 1;", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "try { yield return 1; } finally { Monitor.Exit(Gate); }"));
        Assert.Empty(Assert.Single(run.PairsOn("Value")).First.HeldProtection);
    }

    [Fact]
    public void Handler_after_an_iterator_that_lets_go_before_throwing_holds_nothing()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); try { foreach (var value in _state.Walk()) { } } catch (Exception) { _state.Value = 1; }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "if (DateTime.UtcNow.Ticks > 0) { Monitor.Exit(Gate); throw new Exception(); } yield return 1;"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Empty(new[] { pair.First, pair.Second }.Single(side => side.Symbol.Contains("First.", StringComparison.Ordinal)).HeldProtection);
    }

    [Fact]
    public void Handler_after_an_iterator_that_throws_without_letting_go_stays_protected()
    {
        var run = Analyze(Case("Monitor.Enter(_state.Gate); try { foreach (var value in _state.Walk()) { } } catch (Exception) { _state.Value = 1; }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "if (DateTime.UtcNow.Ticks > 0) throw new Exception(); yield return 1;"));
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Lock_taken_after_the_last_yield_is_not_held_after_break()
    {
        var run = Analyze(Case("try { foreach (var value in _state.Walk()) { break; } _state.Value = 1; } finally { _state.One.Release(); }", "",
                               "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }",
                               "yield return 1; One.Wait();"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Empty(new[] { pair.First, pair.Second }.Single(side => side.Symbol.Contains("First.", StringComparison.Ordinal)).HeldProtection);
    }

    [Fact]
    public void Lock_the_finally_around_a_yield_lets_go_is_not_held_after_break_though_taken_again_later()
    {
        var run = Analyze(Case("try { foreach (var value in _state.Walk()) { break; } _state.Value = 1; } finally { _state.One.Release(); }", "",
                               "_state.One.Wait(); try { _state.Value = 2; } finally { _state.One.Release(); }",
                               "One.Wait(); try { yield return 1; } finally { One.Release(); } One.Wait();"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Empty(new[] { pair.First, pair.Second }.Single(side => side.Symbol.Contains("First.", StringComparison.Ordinal)).HeldProtection);
    }

    [Fact]
    public void Arguments_of_an_iterator_created_in_another_method_bind_through_its_creator()
    {
        var run = Analyze(Case("foreach (var value in _state.Make(3)) { }", "", "foreach (var value in _state.Make(4)) { }",
                               extraState: SLOT_WALKERS));
        Assert.Equal(["[3]", "[4]"], SlotSelectors(run));
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.Resource.AccessPath.Contains("Slots"));
    }

    [Fact]
    public async Task Guard_over_the_argument_of_an_iterators_creation_holds_in_its_body()
    {
        var result = await Findings(Case("IEnumerable<int> sequence = Array.Empty<int>(); var i = _state.Left; " +
                                         "if (i < 4) sequence = _state.WalkAt(i); foreach (var value in sequence) { }", "",
                                         "var j = _state.Right; if (j >= 4) foreach (var value in _state.WalkAt(j)) { }",
                                         extraState: SLOT_WALKERS));
        Assert.Equal(2, result.Accesses.Where(access => access.Resource.AccessPath.Contains("Slots") && access.Resource.Selector is not null)
                                       .Select(access => access.ExecutionId).Distinct().Count());
        Assert.DoesNotContain(result.Findings, finding => finding.Resource.AccessPath.Contains("Slots"));
    }

    [Fact]
    public async Task Guard_over_the_call_that_reaches_an_iterators_creator_holds_in_its_body()
    {
        var result = await Findings(Case("IEnumerable<int> sequence = Array.Empty<int>(); var i = _state.Left; " +
                                         "if (i < 4) sequence = _state.Make(i); foreach (var value in sequence) { }", "",
                                         "var j = _state.Right; if (j >= 4) foreach (var value in _state.WalkAt(j)) { }",
                                         extraState: SLOT_WALKERS));
        Assert.Equal(2, result.Accesses.Where(access => access.Resource.AccessPath.Contains("Slots") && access.Resource.Selector is not null)
                                       .Select(access => access.ExecutionId).Distinct().Count());
        Assert.DoesNotContain(result.Findings, finding => finding.Resource.AccessPath.Contains("Slots"));
    }

    [Fact]
    public async Task Overlapping_guards_over_iterator_creations_keep_the_pair()
    {
        var result = await Findings(Case("IEnumerable<int> sequence = Array.Empty<int>(); var i = _state.Left; " +
                                         "if (i < 5) sequence = _state.WalkAt(i); foreach (var value in sequence) { }", "",
                                         "var j = _state.Right; if (j >= 4) foreach (var value in _state.WalkAt(j)) { }",
                                         extraState: SLOT_WALKERS));
        Assert.Contains(result.Findings, finding => finding.Resource.AccessPath.Contains("Slots") &&
                                                    finding.AccessA.ExecutionId != finding.AccessB.ExecutionId);
    }

    [Fact]
    public void Iterator_that_lets_go_of_the_enumerators_lock_on_one_branch_leaves_the_loop_body_unprotected()
    {
        var run = Analyze(Case("lock (_state.Gate) { foreach (var value in _state.Walk()) { _state.Value = value; } }", "",
                               "lock (_state.Gate) { _state.Value = 2; }",
                               "if (Flag) Monitor.Enter(Gate); else Monitor.Exit(Gate); yield return 1;",
                               "public bool Flag;"));
        var pair = Assert.Single(run.PairsOn("Value"));
        Assert.Empty(new[] { pair.First, pair.Second }.Single(side => side.Symbol.Contains("First.", StringComparison.Ordinal)).HeldProtection);
    }

    /// <summary>An iterator writing the cell its argument names, and a method that creates it and hands it back.</summary>
    private const string SLOT_WALKERS = "public int[] Slots = new int[8]; public int Left; public int Right; " +
                                        "public IEnumerable<int> WalkAt(int index) { Slots[index] = 1; yield return index; } " +
                                        "public IEnumerable<int> Make(int index) => WalkAt(index);";

    private static IReadOnlyList<string> SlotSelectors(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Resource.AccessPath.Contains("Slots") && access.Resource.Selector is not null)
                               .Select(access => access.Resource.Selector!.Text).Order(StringComparer.Ordinal).ToArray();

    private static Task<AnalysisResult> Findings(string source) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", source)), ROOT_DIRECTORY, ProviderRegistry.BuiltIn,
                                      CancellationToken.None);

    private static string Case(string first, string local, string second, string? walk = null, string extraState = "") => Usings + $$"""
        using System.Collections.Generic;
        public sealed class State
        {
            public readonly object Gate = new();
            public readonly SemaphoreSlim One = new(1, 1);
            public IEnumerable<int>? Escaped;
            public int Value;
            public IEnumerable<int> Walk() { {{walk ?? "Value = 1; yield return 1;"}} }
            {{extraState}}
        }
        public sealed class First : BackgroundService
        {
            private readonly State _state;
            public First(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{local}}
                {{first}}
                return Task.CompletedTask;
            }
        }
        public sealed class Second : BackgroundService
        {
            private readonly State _state;
            public Second(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{second}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<First>(); services.AddHostedService<Second>();");
}
