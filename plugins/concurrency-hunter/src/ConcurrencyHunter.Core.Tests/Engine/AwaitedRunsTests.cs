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

    [Fact]
    public void Sync_part_of_a_run_still_meets_the_continuation_of_another()
    {
        foreach (var member in new[] { "Run", "Fire" })
        {
            var run = AwaitedRunsFixture.Run((member == "Run" ? "await " : "") + $"Runs.Lib.{member}(async () => {{ _state.C = 3; await Task.Yield(); _state.C = 1; }});\n_state.C = 2;");
            Assert.Contains(run.PairsOn("C"), pair => pair.First.ExecutionId != pair.Second.ExecutionId &&
                                                    pair.First.Symbol.Contains("Worker", StringComparison.Ordinal) && pair.Second.Symbol.Contains("Worker", StringComparison.Ordinal));
            AwaitedRunsFixture.AssertPairs(run, after: false, self: false);
        }
    }

    [Fact]
    public void Sequential_runs_read_as_concurrent() =>
        AwaitedRunsFixture.AssertPairs(AwaitedRunsFixture.Run("Runs.Lib.Run(async () => { await Task.Yield(); _state.C = 1; Task.Run(() => { }).Wait(); });\n_state.C = 2;",
                                                            "sequential"), after: true, self: true);

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

    internal static EngineRun Run(string work, string relation = "sequential-awaited", bool http = false, string members = "", string other = "",
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

    private static LibraryModels Models(Compilation compilation, string relation, bool builtIn)
    {
        var entries = new[] { "Runs.Lib", "Runs.Holder" }.SelectMany(name => LibrarySource.Value.GetTypeByMetadataName(name)!.GetMembers().OfType<IMethodSymbol>())
            .Where(method => method.MethodKind == MethodKind.Ordinary && method.Name is not ("Unknown" or "Trigger")).Select(method =>
            {
                var fates = method.Parameters.ToDictionary(parameter => parameter.Name, parameter => (object)new Dictionary<string, object>
                {
                    ["fate"] = method.Name == "Keep" ? "holder" : "invoke-now",
                    [method.Name == "Keep" ? "holder" : "runs"] = method.Name == "Keep" ? "result" : method.Name == "Mixed" && parameter.Name == "g" ? "concurrent" : relation
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
