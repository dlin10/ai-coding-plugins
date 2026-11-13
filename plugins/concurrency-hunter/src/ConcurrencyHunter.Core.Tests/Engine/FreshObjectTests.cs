using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Two accesses to fresh objects never pair (R12): an access that reaches a field of an object directly through a value
/// holding only objects one invocation created, including parameters of calls it waits for, touches an object no other invocation
/// touches that way, however far the object has escaped. Every other route to the object still pairs.</summary>
public sealed class FreshObjectTests
{
    private const string POST = "HooksController.Post()";

    [Fact]
    public void Writes_to_fresh_objects_of_two_invocations_make_no_pair()
    {
        var run = Run("var hook = new Hook { Hits = 1 }; hook.Hits = 2; _repository.Items.Add(hook); hook.Hits = 3;");

        var writes = Posts(run, AccessOperation.Write);
        Assert.Equal(3, writes.Count);
        Assert.All(writes, write => Assert.True(write.IsFresh));
        // The object is held by a singleton's list, so it has escaped: only how the writes reach it keeps them apart.
        Assert.All(writes, write => Assert.Equal(OwnershipKind.Escaped, write.Ownership));
        Assert.Empty(run.PairsOn("Hits"));
        Assert.True(run.Skipped(InterproceduralPairing.SKIP_FRESH_OBJECT) > 0);
    }

    [Fact]
    public void Unknown_effect_on_a_fresh_object_and_writes_to_fresh_objects_make_no_pair()
    {
        // The shape of eShop's WebhooksReceivedController.NewWebhook: written in its initializer, put into a singleton's list
        // through a member of the repository, and handed to a call nothing resolves.
        var run = Run("var hook = new Hook { Hits = 1, Data = \"payload\" }; _repository.Add(hook); Opaque.Lib.Touch(hook);");

        var effect = Assert.Single(Posts(run, AccessOperation.UnknownEffect));
        Assert.True(effect.IsFresh);
        Assert.True(Assert.Single(Posts(run, AccessOperation.Write)).IsFresh);
        Assert.Empty(run.PairsOn("Hits"));
        Assert.Empty(run.PairsOn("Data"));
        Assert.True(run.Skipped(InterproceduralPairing.SKIP_FRESH_OBJECT) >= 3);
    }

    [Fact]
    public void Write_to_a_fresh_object_still_pairs_with_a_read_through_its_holder()
    {
        var run = Run("var hook = new Hook(); _repository.Items.Add(hook); hook.Hits = 1;",
                      "foreach (var held in _repository.Items) _ = held.Hits;");

        var write = Assert.Single(Posts(run, AccessOperation.Write));
        Assert.True(write.IsFresh);
        var read = Assert.Single(run.Of("Hits"), access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal));
        Assert.False(read.IsFresh);
        Assert.Contains(run.PairsOn("Hits"), pair => Is(pair, write, read));
        Assert.DoesNotContain(run.PairsOn("Hits"), pair => Is(pair, write, write));
    }

    [Fact]
    public void Write_to_a_fresh_object_still_pairs_with_a_task_that_captures_it()
    {
        var run = Run("var hook = new Hook(); _repository.Items.Add(hook); Task.Run(() => hook.Hits = 1); hook.Hits = 2;");

        // The lambda's write stands in the action's body too, and reaches the object through what it captures.
        var writes = Posts(run, AccessOperation.Write);
        Assert.Equal(2, writes.Count);
        var direct = Assert.Single(writes, write => write.IsFresh);
        var captured = Assert.Single(writes, write => !write.IsFresh);
        Assert.NotEqual(direct.ExecutionId, captured.ExecutionId);
        Assert.Contains(run.PairsOn("Hits"), pair => Is(pair, direct, captured));
    }

    [Fact]
    public void Object_loaded_from_a_field_is_not_fresh()
    {
        // The object is the one this invocation created, but the write reaches it through the singleton's field.
        var run = Run("_repository.Latest = new Hook(); var hook = _repository.Latest; hook.Hits = 1;");

        AssertPairsAsAnyObject(run);
    }

    [Fact]
    public void Value_that_may_be_a_fresh_or_a_loaded_object_is_not_fresh()
    {
        var run = Run("var hook = new Hook(); if (_repository.Items.Count > 0) hook = _repository.Latest; hook.Hits = 1;");

        AssertPairsAsAnyObject(run);
    }

    [Fact]
    public void Field_of_an_object_reached_through_a_fresh_object_is_not_fresh()
    {
        var written = Run("var hook = new Hook { Next = new Hook() }; _repository.Items.Add(hook); hook.Next!.Hits = 1;");
        var touched = Run("var hook = new Hook { Next = new Hook() }; _repository.Items.Add(hook); Opaque.Lib.Touch(hook);");

        var write = Assert.Single(Posts(written, AccessOperation.Write));
        Assert.False(write.IsFresh);
        Assert.Contains(written.PairsOn("Hits"), pair => Is(pair, write, write));
        // The unknown effect touches the fields of the object it is handed as a fresh object's, and the fields of what those
        // fields point to as any other object's.
        var effects = Posts(touched, AccessOperation.UnknownEffect);
        Assert.Equal(2, effects.Count);
        Assert.Contains(effects, effect => effect.IsFresh);
        var inner = Assert.Single(effects, effect => !effect.IsFresh);
        Assert.Contains(touched.PairsOn("Hits"), pair => Is(pair, inner, inner));
    }

    [Fact]
    public void Parameter_bound_to_a_fresh_object_by_a_waited_call_is_fresh()
    {
        var run = Run("var hook = new Hook(); _repository.Items.Add(hook); _repository.Mark(hook);");

        var write = Assert.Single(run.Of("Hits"), access => access.Operation == AccessOperation.Write);
        Assert.Equal("Repository.Mark(Hook)", write.Symbol);
        Assert.True(write.IsFresh);
        Assert.DoesNotContain(run.PairsOn("Hits"), pair => Is(pair, write, write));
    }

    [Fact]
    public void Parameter_bound_by_a_call_not_awaited_is_not_fresh()
    {
        var run = Run("var hook = new Hook(); _repository.Items.Add(hook); _repository.MarkAsync(hook);");

        var write = Assert.Single(run.Of("Hits"), access => access.Operation == AccessOperation.Write);
        Assert.Equal("Repository.MarkAsync(Hook)", write.Symbol);
        Assert.False(write.IsFresh);
        Assert.Contains(run.PairsOn("Hits"), pair => Is(pair, write, write));
    }

    [Fact]
    public void Two_fresh_accesses_are_skipped_under_their_own_reason()
    {
        var run = Run("var hook = new Hook(); _repository.Items.Add(hook); hook.Hits = 1;");

        Assert.Equal("fresh-object", InterproceduralPairing.SKIP_FRESH_OBJECT);
        // The one write, against itself in another invocation of the action.
        Assert.Equal(1, run.Skipped(InterproceduralPairing.SKIP_FRESH_OBJECT));
        Assert.Empty(run.PairsOn("Hits"));
        var reference = ReferencePair(run.Collection.Accesses, run.Execution.Analysis, run.Execution.Heap.Heap);
        Assert.Equal(1, reference.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_FRESH_OBJECT));
    }

    [Fact]
    public async Task Opaque_and_unresolved_receiver_effects_preserve_local_and_handed_freshness()
    {
        foreach (var dispatch in new[] { false, true })
        {
            string Touch(string value) => dispatch ? $"((IHook){value}).Touch();" : $"{value}.Touch();";
            var source = Usings + $$"""
                using System.Collections.Generic;
                public interface IHook { void Touch(); }
                public sealed class KnownHook : IHook { public void Touch() { } }
                public sealed class Hook : IHook
                {
                    public int Hits;
                    public extern void Touch();
                    public void Mark() { {{Touch("this")}} }
                }
                public sealed class Repository
                {
                    public readonly List<Hook> Items = new();
                    public Hook Latest = new();
                    public void Mark(Hook hook) { {{Touch("hook")}} }
                    public async Task MarkAsync(Hook hook) { await Task.Yield(); {{Touch("hook")}} }
                }
                public class HooksController(Repository repository) : ControllerBase
                {
                    public void PostLocal() { var hook = new Hook(); repository.Items.Add(hook); {{Touch("hook")}} }
                    public void PostParameter() { var hook = new Hook(); repository.Items.Add(hook); repository.Mark(hook); }
                    public void PostReceiver() { var hook = new Hook(); repository.Items.Add(hook); hook.Mark(); }
                    public void PostLoaded() { {{Touch("repository.Latest")}} }
                    public void PostDropped() { var hook = new Hook(); repository.Items.Add(hook); _ = repository.MarkAsync(hook); }
                    public void PostDynamic() { var hook = new Hook(); repository.Items.Add(hook); dynamic target = hook; target.Touch(); }
                }
                """ + Startup("services.AddSingleton<Repository>();");
            var solution = FixtureSolution.Create(("Case.cs", source));
            var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
            Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            var run = AnalyzeScope(solution, "scope:Fixture");
            var heapRun = run.Execution.Heap;
            var heap = heapRun.Heap;
            var program = heapRun.Program;
            var scope = new ScopeProgram(program.ScopeId, program.Input.Roots, program.Result, heapRun.Summaries, program.Input.Program,
                                         program.Input.DiIndex, program.Input.InjectionBindings) { MetadataSupertypes = program.MetadataSupertypes };
            var unknown = UnknownCalls.Of(scope, heap);
            foreach (var (symbol, fresh, local, slot) in new (string, bool, bool, HandedSlot?)[]
            {
                ("HooksController.PostLocal()", true, true, null),
                ("Repository.Mark(Hook)", true, false, new(0)),
                ("Hook.Mark()", true, false, new(null)),
                ("HooksController.PostLoaded()", false, false, null),
                ("Repository.MarkAsync(Hook)", false, false, new(0))
            })
            {
                var effects = run.Of("Hits").Where(access => access.Symbol == symbol).ToArray();
                Assert.NotEmpty(effects);
                Assert.All(effects, access =>
                {
                    Assert.Equal(fresh, access.IsFresh);
                    var terminal = Assert.Single(unknown, call => call.Instance.Id == access.InstanceId && call.OperationId == access.OperationId);
                    var hook = access.Resource.RegionId;
                    Assert.Contains(hook, terminal.Receivers.SelectMany(value => heap.Resolve(access.InstanceId, value)));
                    Assert.DoesNotContain(hook, terminal.Arguments.SelectMany(argument => argument.Values)
                        .SelectMany(value => heap.Resolve(access.InstanceId, value)));
                    Assert.Equal(local, terminal.ReceiverIsFresh);
                    Assert.Equal(slot, terminal.ReceiverHandedSlot);
                    if (dispatch)
                    {
                        Assert.Contains((access.InstanceId, access.OperationId), heap.UnresolvedDispatches);
                        var transfer = Assert.Single(terminal.Instance.Summary.Calls, call => call.OperationId == access.OperationId);
                        Assert.Equal(local, transfer.ReceiverIsFresh);
                        Assert.Equal(slot, transfer.ReceiverHandedSlot);
                    }
                    else
                    {
                        var opaque = Assert.Single(terminal.Instance.Summary.OpaqueCalls, call => call.OperationId == access.OperationId);
                        Assert.Equal(local, opaque.ReceiverIsFresh);
                        Assert.Equal(slot, opaque.ReceiverHandedSlot);
                    }
                });
                Assert.Equal(!fresh, run.PairsOn("Hits").Any(pair => effects.Contains(pair.First) && effects.Contains(pair.Second)));
            }
            var dynamicEffects = run.Of("Hits").Where(access => access.Symbol == "HooksController.PostDynamic()").ToArray();
            Assert.NotEmpty(dynamicEffects);
            Assert.All(dynamicEffects, access =>
            {
                var terminal = Assert.Single(unknown, call => call.Instance.Id == access.InstanceId && call.OperationId == access.OperationId);
                Assert.Empty(terminal.Receivers);
                Assert.Contains(access.Resource.RegionId, terminal.Arguments.SelectMany(argument => argument.Values)
                    .SelectMany(value => heap.Resolve(access.InstanceId, value)));
                Assert.True(access.IsFresh);
            });
        }
    }

    // ---- helpers ----

    /// <summary>The action's one write reaches each object it may be as any object's field, and pairs with itself in another
    /// invocation of the action.</summary>
    private static void AssertPairsAsAnyObject(EngineRun run)
    {
        var writes = Posts(run, AccessOperation.Write);
        Assert.NotEmpty(writes);
        Assert.All(writes, write => Assert.False(write.IsFresh));
        Assert.Single(writes.Select(write => (write.Source.StartLine, write.Source.StartColumn)).Distinct());
        Assert.Contains(run.PairsOn("Hits"), pair => writes.Contains(pair.First) && writes.Contains(pair.Second));
        Assert.Equal(0, run.Skipped(InterproceduralPairing.SKIP_FRESH_OBJECT));
    }

    private static IReadOnlyList<Access> Posts(EngineRun run, AccessOperation operation) =>
        run.Of("Hits").Where(access => access.Symbol == POST && access.Operation == operation).ToArray();

    private static bool Is(AccessPair pair, Access one, Access other) =>
        ReferenceEquals(pair.First, one) && ReferenceEquals(pair.Second, other) ||
        ReferenceEquals(pair.First, other) && ReferenceEquals(pair.Second, one);

    private static EngineRun Run(string post, string worker = "") =>
        AnalyzeScope(FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] },
                                            ("Case.cs", Usings + Source(post, worker))),
                     "scope:Fixture");

    /// <summary>A controller action, which overlaps itself, doing <paramref name="post"/>, and a hosted worker doing
    /// <paramref name="worker"/>, over one singleton repository.</summary>
    /// <param name="post">The action's statements.</param>
    /// <param name="worker">The worker's statements.</param>
    private static string Source(string post, string worker) => $$"""
        using System.Collections.Generic;

        public sealed class Hook { public int Hits; public string? Data; public Hook? Next; }

        public sealed class Repository
        {
            public readonly List<Hook> Items = new();
            public Hook Latest = new Hook();
            public void Add(Hook hook) => Items.Add(hook);
            public void Mark(Hook hook) { hook.Hits = 1; }
            public async Task MarkAsync(Hook hook) { await Task.Yield(); hook.Hits = 1; }
        }

        public class HooksController(Repository repository) : ControllerBase
        {
            private readonly Repository _repository = repository;

            public void Post()
            {
                {{post}}
            }
        }

        public sealed class Worker(Repository repository) : BackgroundService
        {
            private readonly Repository _repository = repository;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{worker}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<Repository>(); services.AddHostedService<Worker>();");

    /// <summary>A library the run has no source of: every member is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib
            {
                public static void Touch(object value) { }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
