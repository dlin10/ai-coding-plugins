using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>CoreLib's generation reads the calls of a type a recognizer owns as a program reads them from metadata (question 144,
/// ADR 0019): the type's bodies are never entered, a public member is the recognizer's, and an internal member or field a caller outside
/// CoreLib could not use is handed to code the analysis does not see. The fixture is compiled as <c>System.Private.CoreLib</c>, its own
/// <c>List&lt;T&gt;</c>, <c>Interlocked</c> and <c>Task</c> shadowing the runtime's.</summary>
public sealed class CoreLibRecognizedTypeTests
{
    private const string CORELIB = "System.Private.CoreLib";

    private const string LIBRARY = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace System.Collections.Generic
        {
            public class List<T> : ICollection<T>
            {
                internal T[] _items = new T[4];
                internal int _size;
                public int Version;
                public int Count => _size;
                public bool IsReadOnly => false;
                public void Add(T item) { _items[_size++] = item; }
                internal void Hidden(T item) { _items[0] = item; }
                public void Clear() { _size = 0; }
                public bool Contains(T item) => false;
                public void CopyTo(T[] array, int index) { }
                public bool Remove(T item) => false;
                public IEnumerator<T> GetEnumerator() => null!;
                IEnumerator IEnumerable.GetEnumerator() => null!;
            }
        }

        namespace System.Threading
        {
            public static class Interlocked { public static int Increment(ref int location) => ++location; }
        }

        namespace System.Threading.Tasks
        {
            public class Task { public static Task Run(Action action) { action(); return new Task(); } }
        }

        namespace Lib
        {
            public static class Cache { public static object Last; public static int Count; }
            public static class Api
            {
                MEMBER
            }
        }
        """;

    [Fact]
    public void A_public_member_of_a_recognized_collection_is_the_tables_call_and_its_body_is_never_entered()
    {
        var trace = Run("public static void Run(object x) { var list = new List<object>(); list.Add(x); Cache.Last = list; }", "System.Object");

        var add = Assert.Single(Operations(trace).OfType<IrCallOperation>(), call => call.Method.Contains(".Add(", StringComparison.Ordinal));
        Assert.NotNull(add.Collection);
        Assert.DoesNotContain(trace.Run!.Reachable.Bodies.Keys, body => body.Contains("System.Collections.Generic.List", StringComparison.Ordinal));
    }

    [Fact]
    public void A_public_member_of_Interlocked_is_atomic_and_of_Task_a_spawn()
    {
        var atomic = Run("public static int Run(object x) => Interlocked.Increment(ref Cache.Count);", "System.Object");
        var spawn = Run("public static void Run(Action a) { Task.Run(a); }", "System.Action");

        Assert.Contains(Operations(atomic), operation => operation is IrAtomicOperation);
        Assert.Contains(Operations(spawn), operation => operation is IrSpawnOperation);
        Assert.DoesNotContain(atomic.Run!.Reachable.Bodies.Keys, body => body.Contains("System.Threading.Interlocked", StringComparison.Ordinal));
        Assert.DoesNotContain(spawn.Run!.Reachable.Bodies.Keys, body => body.Contains("System.Threading.Tasks.Task", StringComparison.Ordinal));
    }

    [Fact]
    public void An_internal_member_of_a_recognized_collection_is_an_unresolved_call_of_a_member_without_a_body()
    {
        var trace = Run("public static void Run(object x) { var list = new List<object>(); list.Hidden(x); Cache.Last = list; }", "System.Object");

        var hidden = Assert.Single(Operations(trace).OfType<IrCallOperation>(), call => call.Method.Contains(".Hidden(", StringComparison.Ordinal));
        Assert.Null(hidden.Collection);
        Assert.False(hidden.IsRecognized);
        Assert.Contains(UnknownCalls.Of(trace.Run!.ScopeProgram!, trace.Run.Heap!), call => call.Callee.Contains(".Hidden(", StringComparison.Ordinal));
    }

    [Fact]
    public void An_internal_field_of_a_recognized_collection_read_or_written_elsewhere_is_handed_to_unseen_code_and_a_public_one_is_not()
    {
        var trace = Run("""
            public static void Run(object x)
            {
                var list = new List<object>();
                list._items = new object[] { x, list._size };
                Cache.Count = list.Version;
                Cache.Last = list;
            }
            """, "System.Object");

        Assert.Equal(["field get System.Collections.Generic.List<object>._size", "field set System.Collections.Generic.List<object>._items"],
                     Handed(trace));
        Assert.DoesNotContain(Operations(trace), operation => operation is IrLoadFieldOperation { Field.Name: "_items" or "_size" }
                                                                        or IrStoreFieldOperation { Field.Name: "_items" or "_size" });
        Assert.Contains(Operations(trace), operation => operation is IrLoadFieldOperation { Field.Name: "Version" });
        Assert.Contains(UnknownCalls.Of(trace.Run!.ScopeProgram!, trace.Run.Heap!),
                        call => call.Kind == SemanticGapKinds.DYNAMIC && call.Callee.StartsWith("field set ", StringComparison.Ordinal));
    }

    [Fact]
    public void An_atomic_call_on_an_internal_field_of_a_recognized_collection_is_handed_with_the_value_it_gives_back()
    {
        var trace = Run("public static int Run(object x) { var list = new List<object>(); Cache.Last = list; return Interlocked.Increment(ref list._size); }",
                        "System.Object");

        Assert.Equal(["field set System.Collections.Generic.List<object>._size", "field set System.Collections.Generic.List<object>._size"],
                     Handed(trace));
        Assert.DoesNotContain(Operations(trace), operation => operation is IrAtomicOperation);
    }

    [Fact]
    public void A_call_through_an_interface_on_a_recognized_collection_is_decided_by_the_table_and_never_unresolved()
    {
        var trace = Run("public static void Run(object x) { ICollection<object> list = new List<object>(); list.Add(x); Cache.Last = list; }", "System.Object");

        Assert.DoesNotContain(UnknownCalls.Of(trace.Run!.ScopeProgram!, trace.Run.Heap!), call => call.Callee.Contains("Add(", StringComparison.Ordinal));
    }

    [Fact]
    public void A_member_of_a_type_a_recognizer_owns_but_does_not_claim_runs_its_own_types_bodies()
    {
        // No recognizer claims an awaiter's OnCompleted, so it is generated: its own type stays in the run, every other one stays out.
        var library = Compile(LIBRARY.Replace("MEMBER", "public static void Run(object x) { }", StringComparison.Ordinal) + """
            namespace System.Runtime.CompilerServices
            {
                public readonly struct TaskAwaiter { public void OnCompleted(Action continuation) { continuation(); } }
            }
            """, CORELIB);
        var compilation = library.Compilation!;
        var onCompleted = (IMethodSymbol)Assert.Single(compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.TaskAwaiter")!.GetMembers("OnCompleted"));
        var run = (IMethodSymbol)Assert.Single(compilation.GetTypeByMetadataName("Lib.Api")!.GetMembers("Run"));
        const string AWAITER = "System.Private.CoreLib:System.Runtime.CompilerServices.TaskAwaiter";

        Assert.Contains(AWAITER, ModelGenerator.CoreLibLeftOutTypeKeys(compilation, run));
        Assert.DoesNotContain(AWAITER, ModelGenerator.CoreLibLeftOutTypeKeys(compilation, onCompleted));
        Assert.Contains("System.Private.CoreLib:System.SR", ModelGenerator.CoreLibLeftOutTypeKeys(compilation, onCompleted));
        Assert.Contains(ModelGenerator.CoreLibLeftOutTypeKeys(compilation, onCompleted),
                        key => key.StartsWith("System.Private.CoreLib:System.Collections.Generic.List", StringComparison.Ordinal));
    }

    /// <summary>The generator's trace for <c>Lib.Api.Run</c> of the fixture with <paramref name="member"/> in <c>Api</c>.</summary>
    /// <param name="member">The declaration of <c>Run</c>.</param>
    /// <param name="parameterType">The metadata name of <c>Run</c>'s one parameter.</param>
    private static GenerationTrace Run(string member, string parameterType)
    {
        var trace = TraceOf(LIBRARY.Replace("MEMBER", member, StringComparison.Ordinal), $"M:Lib.Api.Run({parameterType})", CORELIB);
        Assert.True(trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return trace;
    }

    /// <summary>What the accesses of unseen fields in <c>Lib.Api.Run</c> are called as handoffs, in order.</summary>
    /// <param name="trace">The trace, with a run.</param>
    private static string[] Handed(GenerationTrace trace) =>
        Operations(trace).OfType<IrUnknownOperation>().Where(operation => operation.Reason == "unseen-field")
                         .Select(operation => operation.DynamicCallee!).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The operations of the lowered body of <c>Lib.Api.Run</c> in a trace's run.</summary>
    /// <param name="trace">The trace, with a run.</param>
    private static IEnumerable<IrOperation> Operations(GenerationTrace trace) =>
        trace.Run!.Reachable.Bodies.Single(body => body.Key.Contains("Lib.Api.Run", StringComparison.Ordinal)).Value
             .Blocks.SelectMany(block => block.Operations);
}
