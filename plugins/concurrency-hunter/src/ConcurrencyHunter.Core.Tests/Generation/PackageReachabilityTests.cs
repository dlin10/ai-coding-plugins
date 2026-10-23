using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>A package's generation builds its reachable set by constructed types (ADR 0019): a dispatch reaches only the overrides of
/// the types the set constructs, and a call the heap resolves into a body outside the set is an opaque call, never nothing. A package
/// whose method a recognizer claims is still refused whole.</summary>
public sealed class PackageReachabilityTests
{
    private const string RUN = "M:Lib.Api.Run(System.Action)";

    [Fact]
    public void A_package_interface_call_reaches_only_the_overrides_of_constructed_types()
    {
        const string STEP = """
            public interface IStep { void Do(Action a); }
            public sealed class Kept : IStep { public void Do(Action a) { Cache.Last = a; } }
            """;
        const string OTHERS = """
            public sealed class Runs : IStep { public void Do(Action a) { a(); } }
            public sealed class Hands : IStep { public void Do(Action a) { Sink.Take(a); } }
            """;
        const string API = "public static class Api { public static void Run(Action a) { IStep step = new Kept(); step.Do(a); } }";

        var three = Trace(STEP + OTHERS + API, RUN);
        var one = Trace(STEP + API, RUN);

        Assert.True(three.Run is { Stopped: false }, $"{three.Answer.Reason}: {three.Answer.Detail}");
        Assert.Contains(three.Run.Reachable.ReachedBodies.Keys, body => body.Contains(":M:Lib.Kept.Do(", StringComparison.Ordinal));
        Assert.DoesNotContain(three.Run.Reachable.ReachedBodies.Keys, body => body.Contains(":M:Lib.Runs.", StringComparison.Ordinal) ||
                                                                              body.Contains(":M:Lib.Hands.", StringComparison.Ordinal));
        Assert.Equal(Shape(one.Answer), Shape(three.Answer));
    }

    [Fact]
    public void A_package_call_into_a_body_outside_the_set_is_an_opaque_call()
    {
        // The struct is the package's, and no reached body boxes it: its override is outside the set, and the constrained call on it,
        // which the heap resolves by type, is opaque.
        var trace = Trace("""
            public interface IRun { void Go(Action a); }
            public struct Runner : IRun { public void Go(Action a) { a(); } }
            public static class Api
            {
                private static void Call<T>(T runner, Action a) where T : struct, IRun { runner.Go(a); }
                public static void Run(Action a) { Call(new Runner(), a); }
            }
            """, RUN);

        Assert.True(trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        Assert.DoesNotContain(trace.Run.Reachable.ReachedBodies.Keys, body => body.Contains(":M:Lib.Runner.Go(", StringComparison.Ordinal));
        Assert.Contains(UnknownCalls.Of(trace.Run.ScopeProgram!, trace.Run.Heap!), call => call.Callee.Contains(".Go(", StringComparison.Ordinal));
        Assert.Equal(FateClassifier.UNKNOWN_EXECUTION, FateOf(trace, "a").Fate);
    }

    [Fact]
    public void A_package_whose_method_a_recognizer_claims_is_refused_whole()
    {
        var trace = Trace("""
            public static class Api { public static void Run(Action a) { a(); } }
            }
            namespace System.Threading.Tasks { public static class Parallel { public static void For() { } }
            """, RUN);

        Assert.Null(trace.Answer.Classified);
        Assert.Equal(GenerationReasons.ENGINE_RECOGNIZED, trace.Answer.Reason);
        Assert.Contains("Parallel.For", trace.Answer.Detail);
        Assert.Equal(0, trace.Answer.Generation.ReachedBodies);
    }

    /// <summary>What an answer says of the member: its fates, and its model entry or the reason it has none with the codes of its
    /// causes.</summary>
    /// <param name="answer">The answer.</param>
    private static string Shape(GeneratedAnswer answer) =>
        string.Join(", ", (answer.Classified ?? new Dictionary<string, ClassifiedFate>()).OrderBy(fate => fate.Key, StringComparer.Ordinal)
                                                                                     .Select(fate => $"{fate.Key}={fate.Value}")) + " | " +
        (answer.Model is { } model
            ? ModelEntryWriter.Write(model).ToJsonString()
            : $"{answer.Reason ?? answer.ModelReason} [{string.Join(", ", answer.Causes.Select(cause => cause.Code))}]");
}
