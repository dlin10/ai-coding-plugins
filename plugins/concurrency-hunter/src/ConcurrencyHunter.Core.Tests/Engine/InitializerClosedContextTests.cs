using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Heap;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class InitializerClosedContextTests
{
    [Theory]
    [InlineData("load")]
    [InlineData("store")]
    [InlineData("method")]
    [InlineData("property")]
    [InlineData("construct")]
    public void Initializer_keys_follow_generic_method_and_type_contexts(string trigger)
    {
        foreach (var route in new[] { "method", "type", "nested", "two-helpers" })
        foreach (var argument in new[] { "int", "Driver.D" })
        foreach (var treatment in new[] { TypeInitializerTreatment.Inert, TypeInitializerTreatment.Off })
        {
            var binding = route == "nested" ? "System.Collections.Generic.List<U>" : "U";
            var target = "State<" + binding + ">";
            var use = trigger switch
            {
                "load" => $"GC.KeepAlive({target}.Value);",
                "store" => $"{target}.Value = 1;",
                "method" => $"{target}.Touch();",
                "property" => $"GC.KeepAlive({target}.Property);",
                _ => $"GC.KeepAlive(new {target}());"
            };
            var library = $$"""
                using System;
                public static class Shared { public static void Enter() { } }
                public class State<T>
                {
                    public static int Value;
                    static State() { Value = 1; }
                    public static int Property => Value;
                    public static void Touch() { }
                }
                public static class Helper<U> { public static void Read() { {{use}} } }
                """;
            var extra = route == "type" ? "" : route == "two-helpers"
                ? "private static void First<U>() { Second<U>(); } private static void Second<U>() { " + use + " }"
                : "private static void First<U>() { " + use + " }";
            var entry = route == "type" ? $"Helper<{argument}>.Read();" : $"First<{argument}>();";
            var run = TypeInitializerTreatmentFixture.Run(library, entry, treatment, extra);
            var key = argument == "int" ? "System.Private.CoreLib:int" : "Driver:Driver.D";
            var expected = "Library:State<" + (route == "nested" ? $"System.Private.CoreLib:System.Collections.Generic.List<{key}>" : key) + ">";
            var runs = argument == "Driver.D";
            var candidate = run.Reachable.TypeInitializers.SingleOrDefault(candidate => candidate.TypeKey.StartsWith("Library:State<", StringComparison.Ordinal));
            if (treatment == TypeInitializerTreatment.Inert || runs)
            {
                Assert.NotNull(candidate);
                Assert.Contains(expected, candidate.ClosedTypeKeys);
                Assert.DoesNotContain(candidate.ClosedTypeKeys, key => run.Program.IsOpen(key));
            }
            else
            {
                Assert.Null(candidate);
                Assert.DoesNotContain(run.Reachable.ReachedBodies.Keys, body => body.Contains("State`1.#cctor", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void A_helper_reached_with_library_and_driver_arguments_keeps_both_closed_keys()
    {
        const string LIBRARY = "using System; public static class Shared { public static void Enter() { } } " +
            "public class State<T> { public static int Value; static State() { Value = 1; } }";
        var run = TypeInitializerTreatmentFixture.Run(LIBRARY, "Read<int>(); Read<Driver.D>();", TypeInitializerTreatment.Inert,
            "private static void Read<U>() { GC.KeepAlive(State<U>.Value); }");
        var candidate = Assert.Single(run.Reachable.TypeInitializers, candidate => candidate.TypeKey.Contains(":State<", StringComparison.Ordinal));
        Assert.Equal(2, candidate.ClosedTypeKeys.Count);
        Assert.Single(candidate.ClosedTypeKeys, key => ReachableSet.RunsTypeInitializer(run.Program, key, TypeInitializerTreatment.Off,
                                                                                      run.Reachable.OpenWorldAssemblies));
        Assert.Single(candidate.ClosedTypeKeys, key => !ReachableSet.RunsTypeInitializer(run.Program, key, TypeInitializerTreatment.Off,
                                                                                       run.Reachable.OpenWorldAssemblies));
        Assert.DoesNotContain(candidate.ClosedTypeKeys, key => run.Program.IsOpen(key));
    }

    [Fact]
    public void An_open_or_merged_context_still_runs_the_initializer()
    {
        const string LIBRARY = "using System; public static class Shared { public static void Enter() { } } " +
            "public class State<T> { public static int Value; static State() { Value = 1; } }";
        var run = TypeInitializerTreatmentFixture.Run(LIBRARY, "Read<int>(); Read<Driver.D>();", TypeInitializerTreatment.Off,
            "private static void Read<U>() { GC.KeepAlive(State<U>.Value); }", limits: AnalysisLimits.Default with { MaxContextsPerMethod = 0 });
        Assert.Contains(run.Heap!.TypeInitializers, initializer => initializer.TypeKey.Contains(":State<", StringComparison.Ordinal));
        Assert.Contains(run.Reachable.TypeInitializers, candidate => candidate.TypeKey.Contains(":State<", StringComparison.Ordinal));
    }
}
