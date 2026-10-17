using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>CoreLib's generation runs with tracing and stack traces off, as the runtime runs with no listener and no stack trace taken
/// (question 142, ADR 0019): a call, a creation and a field of an <c>EventSource</c>, a type of <c>System.Diagnostics.Tracing</c>,
/// <c>StackTrace</c> or <c>StackFrame</c> do nothing, so none of their bodies is entered and nothing is handed to unseen code. The fixture
/// is compiled as <c>System.Private.CoreLib</c>; its diagnostics reach <c>Lib.Heavy</c>, which nothing else reaches.</summary>
public sealed class CoreLibDiagnosticsOffTests
{
    private const string CORELIB = "System.Private.CoreLib";

    private const string LIBRARY = """
        namespace System.Diagnostics.Tracing
        {
            public class EventSource
            {
                public bool IsEnabled() => Lib.Heavy.Enabled;
                protected void WriteEvent(int eventId, int arg) { Lib.Heavy.Write(arg); }
            }
        }

        namespace System.Buffers
        {
            internal sealed class PoolEventSource : System.Diagnostics.Tracing.EventSource
            {
                internal static readonly PoolEventSource Log = new PoolEventSource();
                public void Rented(int bufferId) { WriteEvent(1, bufferId); }
            }
        }

        namespace System.Diagnostics
        {
            public class StackTrace
            {
                public StackTrace(bool fNeedFileInfo) { Lib.Heavy.Capture(); }
                public override string ToString() => Lib.Heavy.Format();
            }
        }

        namespace Lib
        {
            public static class Heavy
            {
                public static bool Enabled;
                public static void Write(int arg) { Cache.Count = arg; }
                public static void Capture() { Cache.Last = new object(); }
                public static string Format() => Cache.Last.ToString();
            }

            public static class Cache { public static object Last; public static int Count; }
            public static class Api
            {
                MEMBER
            }
        }
        """;

    [Fact]
    public void An_event_source_does_nothing_its_bodies_are_never_entered_and_nothing_is_handed_to_unseen_code()
    {
        var trace = Run("""
            public static void Run(object x)
            {
                var log = System.Buffers.PoolEventSource.Log;
                if (log.IsEnabled())
                    log.Rented(Cache.Count);
                Cache.Last = x;
            }
            """);

        Assert.DoesNotContain(trace.Run!.Reachable.Bodies.Keys, body => body.Contains("EventSource", StringComparison.Ordinal) ||
                                                                         body.Contains("Lib.Heavy", StringComparison.Ordinal));
        Assert.DoesNotContain(Operations(trace), operation => operation is IrCallOperation or IrLoadFieldOperation { Field.Name: "Log" } or IrUnknownOperation);
        Assert.Contains(Operations(trace), operation => operation is IrLoadFieldOperation { Field.Name: "Count" });
        Assert.DoesNotContain(UnknownCalls.Of(trace.Run.ScopeProgram!, trace.Run.Heap!), Diagnostic);
    }

    [Fact]
    public void A_stack_trace_is_never_made_and_its_text_is_a_value_of_its_type_and_nothing_else()
    {
        var trace = Run("public static void Run(object x) { Cache.Last = new System.Diagnostics.StackTrace(true).ToString(); }");

        Assert.DoesNotContain(trace.Run!.Reachable.Bodies.Keys, body => body.Contains("StackTrace", StringComparison.Ordinal) ||
                                                                         body.Contains("Lib.Heavy", StringComparison.Ordinal));
        Assert.DoesNotContain(Operations(trace), operation => operation is IrAllocateOperation or IrCallOperation or IrUnknownOperation);
        Assert.Contains(Operations(trace), operation => operation is IrStoreFieldOperation { Field.Name: "Last" });
        Assert.DoesNotContain(UnknownCalls.Of(trace.Run.ScopeProgram!, trace.Run.Heap!), Diagnostic);
    }

    [Fact]
    public void Diagnostics_are_off_but_for_the_generation_of_a_member_of_their_own()
    {
        var library = Compile(LIBRARY.Replace("MEMBER", "public static void Run(object x) { }", StringComparison.Ordinal), CORELIB);
        var compilation = library.Compilation!;
        var run = (IMethodSymbol)Assert.Single(compilation.GetTypeByMetadataName("Lib.Api")!.GetMembers("Run"));
        var rented = (IMethodSymbol)Assert.Single(compilation.GetTypeByMetadataName("System.Buffers.PoolEventSource")!.GetMembers("Rented"));
        string[] diagnostics = ["System.Private.CoreLib:System.Diagnostics.Tracing.EventSource", "System.Private.CoreLib:System.Buffers.PoolEventSource",
                                "System.Private.CoreLib:System.Diagnostics.StackTrace"];

        Assert.Equal(diagnostics.Order(StringComparer.Ordinal), ModelGenerator.CoreLibSilentTypeKeys(compilation, run).Order(StringComparer.Ordinal));
        Assert.All(diagnostics, key => Assert.Contains(key, ModelGenerator.CoreLibLeftOutTypeKeys(compilation, run)));
        Assert.Empty(ModelGenerator.CoreLibSilentTypeKeys(compilation, rented));
        Assert.All(diagnostics, key => Assert.DoesNotContain(key, ModelGenerator.CoreLibLeftOutTypeKeys(compilation, rented)));
    }

    /// <summary>The generator's trace for <c>Lib.Api.Run(object)</c> of the fixture with <paramref name="member"/> in <c>Api</c>.</summary>
    /// <param name="member">The declaration of <c>Run</c>.</param>
    private static GenerationTrace Run(string member)
    {
        var trace = TraceOf(LIBRARY.Replace("MEMBER", member, StringComparison.Ordinal), "M:Lib.Api.Run(System.Object)", CORELIB);
        Assert.True(trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return trace;
    }

    /// <summary>Whether an unknown call is of the fixture's diagnostics or of what only they reach; the driver's probe calls the
    /// constructor of an <c>object</c> the fixture takes from metadata, which is one all the same.</summary>
    /// <param name="call">The unknown call.</param>
    private static bool Diagnostic(UnknownCall call) =>
        call.Callee.Contains("EventSource", StringComparison.Ordinal) || call.Callee.Contains("StackTrace", StringComparison.Ordinal) ||
        call.Callee.Contains("Heavy", StringComparison.Ordinal);

    /// <summary>The operations of the lowered body of <c>Lib.Api.Run</c> in a trace's run.</summary>
    /// <param name="trace">The trace, with a run.</param>
    private static IEnumerable<IrOperation> Operations(GenerationTrace trace) =>
        trace.Run!.Reachable.Bodies.Single(body => body.Key.Contains("Lib.Api.Run", StringComparison.Ordinal)).Value
             .Blocks.SelectMany(block => block.Operations);
}
