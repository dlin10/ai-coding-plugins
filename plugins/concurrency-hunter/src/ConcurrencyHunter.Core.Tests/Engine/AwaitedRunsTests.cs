using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class AwaitedRunsTests
{
    [Fact]
    public void Built_in_sequential_awaited_record_has_both_effects() =>
        AwaitedRunsFixture.AssertPairs(AwaitedRunsFixture.Run(AwaitedRunsFixture.Direct, builtIn: true), after: false, self: false);

    /// <summary>A delegate whose run writes 3 in its synchronous part and 1 in its continuation.</summary>
    private const string SyncThenAwait = "async () => { _state.C = 3; await Task.Yield(); _state.C = 1; }";

    [Fact]
    public void Sync_part_of_a_run_still_meets_the_continuation_of_another_outside_one_awaited_entry()
    {
        // The call sits in the root body, which no async call enters; the void member gives no task to await.
        foreach (var member in new[] { "Run", "Fire" })
        {
            var run = AwaitedRunsFixture.Run((member == "Run" ? "await " : "") + $"Runs.Lib.{member}({SyncThenAwait});\n_state.C = 2;");
            AwaitedRunsFixture.AssertNextRun(run, pair: true);
            AwaitedRunsFixture.AssertPairs(run, after: false, self: false);
        }
    }

    [Fact]
    public void Continuation_of_a_run_precedes_the_next_sync_part_where_one_awaited_entry_awaits_the_call_at_once() =>
        AwaitedRunsFixture.AssertNextRun(AwaitedRunsFixture.Run("await Helper();", members: $"private async Task Helper() {{ await Runs.Lib.Run({SyncThenAwait}); }}"),
                                         pair: false);

    [Fact]
    public void Continuation_meets_the_next_sync_part_when_the_call_task_is_awaited_after_the_prefix() =>
        AwaitedRunsFixture.AssertNextRun(AwaitedRunsFixture.Run("await Helper();", members:
            $"private async Task Helper() {{ var t = Runs.Lib.Run({SyncThenAwait}); await Task.Yield(); await t; }}"), pair: true);

    [Fact]
    public void Continuation_meets_the_next_sync_part_when_an_operation_between_the_call_and_its_await_may_throw() =>
        AwaitedRunsFixture.AssertNextRun(AwaitedRunsFixture.Run("await Helper();", members:
            $"private async Task Helper() {{ var t = Runs.Lib.Run({SyncThenAwait}); _state.Choose = true; await t; }}"), pair: true);

    [Fact]
    public void Continuation_meets_the_next_sync_part_when_the_body_has_two_async_entries() =>
        AwaitedRunsFixture.AssertNextRun(AwaitedRunsFixture.Run("await Helper();\nawait Helper();", members:
            $"private async Task Helper() {{ await Runs.Lib.Run({SyncThenAwait}); }}"), pair: true);

    [Fact]
    public void Continuation_meets_the_next_sync_part_when_the_entry_does_not_complete_before_its_next_start() =>
        AwaitedRunsFixture.AssertNextRun(AwaitedRunsFixture.Run("for (var i = 0; i < 2; i++) { _ = Helper(); }", members:
            $"private async Task Helper() {{ await Runs.Lib.Run({SyncThenAwait}); }}"), pair: true);

    [Fact]
    public void Sequential_runs_read_as_concurrent() =>
        AwaitedRunsFixture.AssertPairs(AwaitedRunsFixture.Run("Runs.Lib.Run(async () => { await Task.Yield(); _state.C = 1; Task.Run(() => { }).Wait(); });\n_state.C = 2;",
                                                            "sequential"), after: true, self: true);

    [Fact]
    public void Sequential_runs_order_the_work_each_run_waits_for() =>
        AwaitedRunsFixture.AssertPairs(AwaitedRunsFixture.Run($"Runs.Lib.Run({AwaitedRunsFixture.SyncBody});\n_state.C = 2;", "sequential"),
                                       after: false, self: false);

    [Fact]
    public void Sequential_runs_outside_the_admission_order_only_the_code_after_the_call()
    {
        var cases = new[]
        {
            AwaitedRunsFixture.Run($"Runs.Lib.Run({AwaitedRunsFixture.SyncBody});\n_state.C = 2;", "sequential", generated: true),
            AwaitedRunsFixture.Run($"Go({AwaitedRunsFixture.SyncBody});\n_state.C = 2;", "sequential",
                                   members: "private void Go(Func<Task> f) { Runs.Lib.Run(f); }"),
            AwaitedRunsFixture.Run($"Func<Task> f = {AwaitedRunsFixture.SyncBody}; f += () => Task.CompletedTask; Runs.Lib.Run(f);\n_state.C = 2;",
                                   "sequential"),
            AwaitedRunsFixture.Run($"Runs.Lib.Run({AwaitedRunsFixture.SyncBody});\n_state.C = 2;", relation: null)
        };
        foreach (var run in cases)
            AwaitedRunsFixture.AssertPairs(run, after: false, self: true);
    }

    /// <summary>A non-async target runs as the async call whose task it returns only where one run of the target makes that call once
    /// (R8): around a <c>do</c>, <c>for</c>, <c>while</c>, <c>foreach</c> or <c>goto</c> loop it returns the last run's task, and the
    /// earlier runs are work it leaves behind, which overlaps the next run and the code after the call. A target that waits each earlier
    /// run inside the loop is read the same way, since nothing proves its waits for the admission. A return on the loop's first pass and
    /// a plain lambda make the call once.</summary>
    /// <param name="form">The target's form, which names the delegate the curated call runs.</param>
    /// <param name="paired">Whether the async call's work keeps its self-pair and its pair with the write after the call.</param>
    [Theory]
    [InlineData("do", true)]
    [InlineData("for", true)]
    [InlineData("while", true)]
    [InlineData("foreach", true)]
    [InlineData("goto", true)]
    [InlineData("do-waiting", true)]
    [InlineData("first-pass-return", false)]
    [InlineData("lambda", false)]
    public void Non_async_target_making_its_returned_call_more_than_once_keeps_the_pairs(string form, bool paired)
    {
        var target = form switch
        {
            "do" => "() => { Task t; var i = 0; do { t = WorkAsync(); i++; } while (i < 2); return t; }",
            "for" => "() => { Task t = null!; for (var i = 0; i < 2; i++) t = WorkAsync(); return t; }",
            "while" => "() => { Task t = null!; var i = 0; while (i < 2) { t = WorkAsync(); i++; } return t; }",
            "foreach" => "() => { Task t = null!; foreach (var x in new[] { 1, 2 }) t = WorkAsync(); return t; }",
            "goto" => "() => { Task t; var i = 0; again: t = WorkAsync(); if (++i < 2) goto again; return t; }",
            "do-waiting" => "() => { Task t; var i = 0; do { t = WorkAsync(); if (i == 0) t.Wait(); i++; } while (i < 2); return t; }",
            "first-pass-return" => "() => { for (;;) return WorkAsync(); }",
            "lambda" => "() => WorkAsync()",
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        AwaitedRunsFixture.AssertPairs(AwaitedRunsFixture.Run($"await Runs.Lib.Run({target});\n_state.C = 2;"), after: paired, self: paired);
    }

    [Fact]
    public void Wait_of_a_call_task_that_passes_the_gate_is_no_unproven_join()
    {
        var run = AwaitedRunsFixture.Run(AwaitedRunsFixture.Direct);
        Assert.DoesNotContain(run.Execution.Analysis.UnprovenJoins, site => site.BodyId.EndsWith("Worker.ExecuteAsync(System.Threading.CancellationToken)", StringComparison.Ordinal));
        AwaitedRunsFixture.AssertPairs(run, after: false, self: false);
    }

    [Fact]
    public void Wait_of_a_call_task_that_fails_the_gate_stays_an_unproven_join()
    {
        var run = AwaitedRunsFixture.Run("for (var i = 0; i < 2; i++) { " + AwaitedRunsFixture.Direct + " }");
        Assert.Contains(run.Execution.Analysis.UnprovenJoins, site => site.BodyId.EndsWith("Worker.ExecuteAsync(System.Threading.CancellationToken)", StringComparison.Ordinal));
        AwaitedRunsFixture.AssertPairs(run, after: true, self: true);
    }

    [Fact]
    public void Relation_of_a_generated_layer_call_proves_no_order()
    {
        var run = AwaitedRunsFixture.Run(AwaitedRunsFixture.Direct, generated: true);
        Assert.Empty(run.Execution.Heap.Heap.CallTasks);
        AwaitedRunsFixture.AssertPairs(run, after: true, self: true);
    }

    [Fact]
    public void Holder_triggers_keep_todays_reading()
    {
        var run = AwaitedRunsFixture.Run("var holder = Runs.Lib.Keep(async () => { await Task.Yield(); _state.C = 1; }); holder.Trigger();\n_state.C = 2;");
        Assert.Contains(run.Execution.Heap.Heap.RunRelations, run => run.Relation == RunRelation.Concurrent);
        AwaitedRunsFixture.AssertPairs(run, after: true, self: true);
    }

    [Fact]
    public void Held_delegate_run_at_the_same_call_keeps_both_pairs()
    {
        foreach (var builtIn in new[] { false, true })
        {
            foreach (var sharedDelegate in new[] { false, true })
            {
                var run = AwaitedRunsFixture.Run("Func<Task> f = async () => { await Task.Yield(); _state.C = 1; }; " +
                    "var holder = Runs.Lib.Keep(f); await holder.Run(" +
                    (sharedDelegate ? "f" : "async () => { await Task.Yield(); _state.Choose = true; }") + ");\n_state.C = 2;", builtIn: builtIn);
                Assert.Contains(run.Execution.Heap.Heap.RunRelations, relation => relation.Relation == RunRelation.Concurrent);
                Assert.Contains(run.Execution.Heap.Heap.RunRelations, relation => relation.Relation == RunRelation.SequentialAwaited);
                AwaitedRunsFixture.AssertPairs(run, after: true, self: true);
            }
        }
    }

    [Fact]
    public void ContinueWith_on_a_call_task_keeps_todays_reading()
    {
        var run = AwaitedRunsFixture.Run("var t = Runs.Lib.Run(async () => { await Task.Yield(); _state.C = 1; });\nt.ContinueWith(_ => { _state.C = 2; });");
        AwaitedRunsFixture.AssertPairs(run, after: true, self: false);
    }

    [Fact]
    public void Call_task_returned_through_an_async_completion_is_proven()
    {
        var run = AwaitedRunsFixture.Run("var t = await MakeAsync();\nawait t;\n_state.C = 2;", members:
            "private async Task<Task> MakeAsync() { await Task.Yield(); return Runs.Lib.Run(async () => { await Task.Yield(); _state.C = 1; }); }");
        AwaitedRunsFixture.AssertPairs(run, after: false, self: false);
    }

    [Fact]
    public void A_null_store_on_the_other_path_does_not_prove_a_call_task_field_join()
    {
        var run = AwaitedRunsFixture.Run("var t = Runs.Lib.Run(async () => { await Task.Yield(); _state.C = 1; });\n" +
                                        "if (_state.Choose) { _state.Task = t; } else { _state.Task = null; }\ntry { await _state.Task; } catch { }\n_state.C = 2;");
        Assert.Contains(run.Execution.Analysis.UnprovenJoins, site => site.BodyId.EndsWith("Worker.ExecuteAsync(System.Threading.CancellationToken)", StringComparison.Ordinal));
        AwaitedRunsFixture.AssertPairs(run, after: true, self: false);
    }

    [Fact]
    public void Call_tasks_and_relations_survive_worklist_rebuild_and_audit()
    {
        var run = AwaitedRunsFixture.Run(AwaitedRunsFixture.Direct);
        AwaitedRunsFixture.AssertPairs(run, after: false, self: false);
        var original = run.Execution.Heap.Heap;
        var program = run.Execution.Heap.Program;
        var scope = new ScopeProgram(program.ScopeId, program.Input.Roots, program.Result, run.Execution.Heap.Summaries,
                                     program.Input.Program, program.Input.DiIndex, program.Input.InjectionBindings)
        {
            MetadataSupertypes = program.MetadataSupertypes
        };
        var audited = WholeProgram.Solve(scope, AnalysisLimits.Default, CancellationToken.None, new(Audit: true, VerifyConvergence: true));
        Assert.NotEmpty(original.CallTasks);
        Assert.True(original.RunRelations.SetEquals(audited.RunRelations));
        Assert.Equal(original.CallTasks.OrderBy(pair => pair.Key), audited.CallTasks.OrderBy(pair => pair.Key));
    }
}

internal static class AwaitedRunsFixture
{
    internal const string Direct = "await Runs.Lib.Run(async () => { await Task.Yield(); _state.C = 1; });\n_state.C = 2;";

    /// <summary>A synchronous delegate body that starts its own work and waits for it before it returns.</summary>
    internal const string SyncBody = "() => { Task.Run(() => { _state.C = 1; }).Wait(); return Task.CompletedTask; }";
    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Runs", [CSharpSyntaxTree.ParseText("""
        using System;
        using System.Threading.Tasks;
        namespace Runs;
        public class Holder { public void Trigger() { } public Task Run(Func<Task> f) => null!; }
        public static class Lib
        {
            public static Task Run(Func<Task> f) => null!;
            public static ValueTask RunValue(Func<ValueTask> f) => default;
            public static Task<int> TaskOf(Func<Task<int>> f) => null!;
            public static ValueTask<int> ValueOf(Func<ValueTask<int>> f) => default;
            public static Task<int> TaskBare(Func<Task<int>> f) => null!;
            public static ValueTask<int> ValueBare(Func<ValueTask<int>> f) => default;
            public static void Fire(Func<Task> f) { }
            public static Task Two(Func<Task> f, Func<Task> g) => null!;
            public static Task Mixed(Func<Task> f, Func<Task> g) => null!;
            public static Func<Task> Unknown() => null!;
            public static Holder Keep(Func<Task> f) => null!;
        }
        """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        using var stream = new MemoryStream();
        var emitted = LibrarySource.Value.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });

    internal static string Consume(string expression, string wait) => wait switch
    {
        "await" => "await " + expression + ";",
        "wait" => expression + ".Wait();",
        "get" => expression + ".GetAwaiter().GetResult();",
        "result" => "_ = " + expression + ".Result;",
        "none" => expression + ";",
        _ => throw new ArgumentOutOfRangeException(nameof(wait))
    };

    internal static EngineRun Run(string work, string? relation = "sequential-awaited", bool http = false, string members = "", string other = "",
                                  bool builtIn = false, bool generated = false, bool fieldDelegate = false)
    {
        var source = Usings + $$"""
            public sealed class State
            {
                public int C;
                public bool Choose;
                public Task Task;
                public Func<Task> F;
                {{(fieldDelegate ? "public State() { F = async () => { await Task.Yield(); C = 1; }; }" : "")}}
            }
            {{(http ? "[ApiController, Route(\"runs\")] public sealed class Worker(State state) : ControllerBase" : "public sealed class Worker(State state) : BackgroundService")}}
            {
                private readonly State _state = state;
                {{(http ? "[HttpGet] public async Task Work()" : "protected override async Task ExecuteAsync(CancellationToken stoppingToken)")}}
                {
                    {{work}}
                }
                private async Task WorkAsync() { await Task.Yield(); _state.C = 1; }
                {{members}}
            }
            public sealed class Reader(State state) : BackgroundService
            {
                private readonly State _state = state;
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{(other.StartsWith("// http", StringComparison.Ordinal) ? "" : other)}}
                }
            }
            [ApiController, Route("reader")]
            public sealed class ReaderController(State state) : ControllerBase
            {
                private readonly State _state = state;
                [HttpGet] public async Task Read() { {{(other.StartsWith("// http", StringComparison.Ordinal) ? other : "")}} }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddSingleton<object>(_ => new object()); " +
                           (http ? "" : "services.AddHostedService<Worker>(); ") + "services.AddHostedService<Reader>();");
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", source));
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var models = Models(compilation, relation, builtIn);
        if (generated)
        {
            var reached = ReachScope(solution, "scope:Fixture", models);
            foreach (var call in reached.Result.Bodies.Values.SelectMany(body => body.Blocks).SelectMany(block => block.Operations).OfType<IrCallOperation>()
                                        .Where(call => call.Library?.MemberId.StartsWith("M:Runs.", StringComparison.Ordinal) == true))
            {
                // The generated reader rejects runs; this deliberately constructs that IR boundary to test Analysis independently.
                var property = typeof(IrCallOperation).GetProperty(nameof(IrCallOperation.Library))!;
                property.SetValue(call, call.Library! with { Layer = IrModelLayer.Generated });
            }
            var execution = Execute(Solve(reached));
            var collection = Collect(execution);
            return new EngineRun(execution, collection, InterproceduralPairing.Pair(collection.Accesses, execution.Analysis, execution.Heap.Heap));
        }
        return AnalyzeScope(solution, "scope:Fixture", models);
    }

    internal static void AssertPairs(EngineRun run, bool after, bool self)
    {
        var writes = run.Of("C").Where(access => access.Operation == AccessOperation.Write).ToArray();
        var body = run.Execution.Heap.Program.Result.Bodies.Values;
        var continuationSites = body.SelectMany(item => item.Blocks.SelectMany(block => block.Operations).OfType<IrStoreFieldOperation>()
                                    .Where(store => store.Field.Name == "C" && item.Values.Any(value => value.Id == store.Value && value.Kind == IrValueKind.Constant && value.Name == "1"))
                                    .Select(store => (item.BodyId, store.Id))).ToHashSet();
        var continuations = writes.Where(access => continuationSites.Contains((access.BodyId, access.OperationId)) &&
            run.Execution.Analysis.Executions.Any(execution => execution.Id == access.ExecutionId && execution.Kind == ExecutionKind.Spawn)).ToArray();
        Assert.NotEmpty(continuations);
        var afterSites = body.SelectMany(item => item.Blocks.SelectMany(block => block.Operations).OfType<IrStoreFieldOperation>()
                             .Where(store => store.Field.Name == "C" && item.Values.Any(value => value.Id == store.Value && value.Kind == IrValueKind.Constant && value.Name == "2"))
                             .Select(store => (item.BodyId, store.Id))).ToHashSet();
        Assert.Contains(writes, access => afterSites.Contains((access.BodyId, access.OperationId)));
        var pairs = run.PairsOn("C");
        bool Continuation(Access access) => continuations.Contains(access);
        bool After(Access access) => afterSites.Contains((access.BodyId, access.OperationId));
        Assert.Equal(after, pairs.Any(pair => Continuation(pair.First) && After(pair.Second) || Continuation(pair.Second) && After(pair.First)));
        Assert.Equal(self, pairs.Any(pair => Continuation(pair.First) && pair.First == pair.Second));
    }

    /// <summary>Asserts whether a continuation of a run, writing 1, still meets the synchronous part of a run, writing 3 before its
    /// first await.</summary>
    /// <param name="run">The analyzed case.</param>
    /// <param name="pair">Whether the pair is expected.</param>
    internal static void AssertNextRun(EngineRun run, bool pair)
    {
        var body = run.Execution.Heap.Program.Result.Bodies.Values;
        HashSet<(string BodyId, int Id)> Stores(string constant) =>
            body.SelectMany(item => item.Blocks.SelectMany(block => block.Operations).OfType<IrStoreFieldOperation>()
                .Where(store => store.Field.Name == "C" && item.Values.Any(value => value.Id == store.Value && value.Kind == IrValueKind.Constant && value.Name == constant))
                .Select(store => (item.BodyId, store.Id))).ToHashSet();
        var continuationSites = Stores("1");
        var syncSites = Stores("3");
        bool Continuation(Access access) => continuationSites.Contains((access.BodyId, access.OperationId)) &&
            run.Execution.Analysis.Executions.Any(execution => execution.Id == access.ExecutionId && execution.Kind == ExecutionKind.Spawn);
        bool Sync(Access access) => syncSites.Contains((access.BodyId, access.OperationId));
        var writes = run.Of("C").Where(access => access.Operation == AccessOperation.Write).ToArray();
        Assert.Contains(writes, Continuation);
        Assert.Contains(writes, Sync);
        Assert.Equal(pair, run.PairsOn("C").Any(item => Continuation(item.First) && Sync(item.Second) || Continuation(item.Second) && Sync(item.First)));
    }

    private static LibraryModels Models(Compilation compilation, string? relation, bool builtIn)
    {
        var entries = new[] { "Runs.Lib", "Runs.Holder" }.SelectMany(name => LibrarySource.Value.GetTypeByMetadataName(name)!.GetMembers().OfType<IMethodSymbol>())
            .Where(method => method.MethodKind == MethodKind.Ordinary && method.Name is not ("Unknown" or "Trigger")).Select(method =>
            {
                var fates = method.Parameters.ToDictionary(parameter => parameter.Name, parameter =>
                {
                    var fate = new Dictionary<string, object> { ["fate"] = method.Name == "Keep" ? "holder" : "invoke-now" };
                    var runs = method.Name == "Keep" ? "result" : method.Name == "Mixed" && parameter.Name == "g" ? "concurrent" : relation;
                    if (runs is not null)
                        fate[method.Name == "Keep" ? "holder" : "runs"] = runs;
                    return (object)fate;
                });
                var entry = new Dictionary<string, object> { ["member"] = DocumentationCommentId.CreateDeclarationId(method)!, ["effects"] = new { }, ["fates"] = fates };
                if (method.Name is "TaskOf" or "ValueOf")
                    entry["result"] = "task([completion(returns:f)])";
                return entry;
            });
        if (builtIn)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, assemblies = new[] { "Runs" },
                versions = new { minimum = "0.0.0.0", maximumExclusive = "1.0.0.0" }, models = entries
            });
            var table = BuiltInModelReader.Read(bytes);
            return new LibraryModels(table.Members, table.ImmutableTypes);
        }
        var root = Directory.CreateTempSubdirectory("ch-awaited-runs-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, ".concurrency-hunter", "models")).FullName;
            File.WriteAllText(Path.Combine(folder, "runs.json"), JsonSerializer.Serialize(new { schemaVersion = 1, assemblies = new[] { "Runs" }, models = entries }), new UTF8Encoding(false));
            var files = ProjectModelFiles.Read(root);
            var (models, rejections) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(root, files));
            Assert.Empty(rejections);
            return models;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
