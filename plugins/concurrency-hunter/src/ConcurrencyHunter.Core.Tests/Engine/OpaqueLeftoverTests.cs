using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The two leftovers of the 4b review closed in phase 5b (R7): a constructor without a body writes its <c>out</c> arguments
/// where the object is created (open question 23), and an iterator a delegate captured escapes with the delegate a field keeps (open
/// question 24).</summary>
public sealed class OpaqueLeftoverTests
{
    // ---- open question 23 ----

    [Fact]
    public void Constructor_without_a_body_writes_its_out_argument_where_the_object_is_created()
    {
        var run = Run("GC.KeepAlive(new Opaque.External(out _state.Count));", "_ = _state.Count;");

        var write = Assert.Single(run.Of("Count"), access => access.Operation == AccessOperation.Write);
        Assert.Equal("Worker.ExecuteAsync(CancellationToken)", write.Symbol);
        Assert.NotEmpty(run.PairsOn("Count"));
    }

    [Fact]
    public void Constructor_with_a_body_writes_its_out_argument_in_its_body_as_before()
    {
        var run = Run("GC.KeepAlive(new Local(out _state.Count));", "_ = _state.Count;");

        var write = Assert.Single(run.Of("Count"), access => access.Operation == AccessOperation.Write);
        Assert.Equal("Local..ctor(int)", write.Symbol);
        Assert.NotEmpty(run.PairsOn("Count"));
    }

    [Fact]
    public void Constructor_without_a_body_writing_a_local_touches_no_field()
    {
        var run = Run("GC.KeepAlive(new Opaque.External(out var local)); GC.KeepAlive(local);", "_ = _state.Count;");

        Assert.DoesNotContain(run.Of("Count"), access => access.Operation == AccessOperation.Write);
    }

    // ---- open question 24 ----

    [Fact]
    public void Iterator_captured_by_a_lambda_a_singleton_field_keeps_is_enumerated_by_an_unknown_execution()
    {
        var run = Run("var items = Sequence.Numbers(_state); _state.Callback = () => { foreach (var item in items) { } };", "_ = _state.Count;");

        Assert.Single(run.Execution.Heap.Heap.UnknownIterators);
        var write = Assert.Single(run.Of("Count"), access => access.Operation == AccessOperation.Write);
        Assert.Equal(ExecutionKind.UnknownEnumeration, run.Execution.Analysis.Execution(write.ExecutionId).Kind);
        Assert.NotEmpty(run.PairsOn("Count"));
    }

    [Fact]
    public void Iterator_captured_by_a_local_lambda_nobody_calls_is_never_enumerated()
    {
        var run = Run("var items = Sequence.Numbers(_state); Action local = () => { foreach (var item in items) { } }; GC.KeepAlive(local);",
                      "_ = _state.Count;");

        Assert.Empty(run.Execution.Heap.Heap.UnknownIterators);
        Assert.DoesNotContain(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownEnumeration);
        Assert.DoesNotContain(run.Of("Count"), access => access.Symbol.StartsWith("Sequence.", StringComparison.Ordinal));
    }

    [Fact]
    public void Lambda_a_field_keeps_without_an_iterator_in_its_capture_enumerates_nothing()
    {
        var run = Run("var state = _state; _state.Callback = () => state.Count = 3;", "_ = _state.Count;");

        Assert.Empty(run.Execution.Heap.Heap.UnknownIterators);
        Assert.DoesNotContain(run.Execution.Analysis.Executions, execution => execution.Kind is ExecutionKind.UnknownEnumeration);
        // Stored without a visible call, the delegate itself runs nowhere of its own.
        Assert.DoesNotContain(run.Execution.Analysis.Executions, execution => execution.Kind is ExecutionKind.UnknownDelegateCall);
    }

    // ---- helpers ----

    private static EngineRun Run(string work, string other) =>
        AnalyzeScope(FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] }, ("Case.cs", Source(work, other))),
                     "scope:Fixture");

    /// <summary>A singleton <c>State</c> one worker does <paramref name="work"/> on while another does <paramref name="other"/>.</summary>
    private static string Source(string work, string other) => Usings + $$"""
        using System.Collections.Generic;

        public sealed class State
        {
            public int Count;
            public Action? Callback;
        }

        public sealed class Local { public Local(out int value) => value = 1; }

        public static class Sequence
        {
            public static IEnumerable<int> Numbers(State state)
            {
                state.Count = 1;
                yield return 1;
            }
        }

        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }

        public sealed class Reader(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{other}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();");

    /// <summary>A library the run has no source of, with a constructor that writes an <c>out</c> argument.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public sealed class External { public External(out int value) => value = 0; }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
