using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>Which rules the reachable set of a generation is built by, by the assembly generated (ADR 0019): dispatch by constructed
/// types for every library, and <c>System.SR</c>, the types a recognizer owns and the diagnostics left out for
/// <c>System.Private.CoreLib</c> alone. Each assembly compiles the same source, which declares all three, under its own name.</summary>
public sealed class ReachabilityRuleSelectionTests
{
    /// <summary>The assembly generated.</summary>
    public enum Assembly
    {
        /// <summary><c>System.Private.CoreLib</c>.</summary>
        CoreLib,

        /// <summary>A package's assembly.</summary>
        Package,

        /// <summary>An assembly of the framework other than CoreLib.</summary>
        FrameworkAssembly
    }

    /// <summary>A part of the rules.</summary>
    public enum Part
    {
        /// <summary>Dispatch by constructed types.</summary>
        Dispatch,

        /// <summary><c>System.SR</c> left out.</summary>
        SrLeftOut,

        /// <summary>A type a recognizer owns left out.</summary>
        RecognizedTypeLeftOut,

        /// <summary>The diagnostics left out and silent.</summary>
        DiagnosticsSilent
    }

    /// <summary>A cell of the matrix.</summary>
    /// <param name="Assembly">The assembly generated.</param>
    /// <param name="Part">The part of the rules.</param>
    private sealed record Cell(Assembly Assembly, Part Part)
    {
        public override string ToString() => $"{Assembly}/{Part}";
    }

    private const string LIBRARY = """
        namespace System
        {
            internal static class SR { public static string Format(string text) => text; }
        }

        namespace System.Threading
        {
            public static class Interlocked { public static int Increment(ref int location) => ++location; }
        }

        namespace System.Diagnostics.Tracing
        {
            public class EventSource { public bool IsEnabled() => false; }
        }

        namespace Lib
        {
            public static class Cache { public static object Last; }
            public static class Api { public static void Run(object x) { Cache.Last = x; } }
        }
        """;

    /// <summary>The library compiled under each assembly's name.</summary>
    private static readonly Dictionary<Assembly, Lazy<Compilation>> Compilations = Enum.GetValues<Assembly>().ToDictionary(
        assembly => assembly, assembly => new Lazy<Compilation>(() => Compile(LIBRARY, NameOf(assembly)).Compilation!));

    private static string NameOf(Assembly assembly) => assembly switch
    {
        Assembly.CoreLib => "System.Private.CoreLib",
        Assembly.Package => ASSEMBLY,
        Assembly.FrameworkAssembly => "System.Linq",
        _ => throw new ArgumentOutOfRangeException(nameof(assembly), assembly, null)
    };

    private static IEnumerable<Cell> Cells() =>
        from assembly in Enum.GetValues<Assembly>()
        from part in Enum.GetValues<Part>()
        select new Cell(assembly, part);

    /// <summary>Whether the part holds for the cell's assembly (R1): dispatch by constructed types for every library, the rest for CoreLib
    /// alone.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Expected(Cell cell) => cell.Part == Part.Dispatch || cell.Assembly == Assembly.CoreLib;

    /// <summary>What the rules show of the cell's part, each of which holds exactly when the part does.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="compilation">The library compiled as the cell's assembly.</param>
    /// <param name="rules">The rules of the generation of <c>Lib.Api.Run</c>.</param>
    private static IEnumerable<(string What, bool Holds)> Observed(Cell cell, Compilation compilation, ReachabilityRules rules)
    {
        string Key(string type) => SymbolNames.TypeKey(compilation.GetTypeByMetadataName(type)!);
        switch (cell.Part)
        {
            case Part.Dispatch:
                yield return ("dispatch by constructed types", rules.Dispatch == DispatchRule.ConstructedTypes);
                break;
            case Part.SrLeftOut:
                yield return ("System.SR left out", rules.LeftOutTypeKeys.Contains(Key("System.SR")));
                break;
            case Part.RecognizedTypeLeftOut:
                yield return ("Interlocked left out", rules.LeftOutTypeKeys.Contains(Key("System.Threading.Interlocked")));
                break;
            case Part.DiagnosticsSilent:
                var eventSource = Key("System.Diagnostics.Tracing.EventSource");
                yield return ("EventSource left out", rules.LeftOutTypeKeys.Contains(eventSource));
                yield return ("EventSource silent", rules.SilentTypeKeys.Contains(eventSource));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cell), cell, null);
        }
    }

    [Fact]
    public void Every_cell_gets_its_expected_rule()
    {
        var failures = new List<string>();
        // bounded: the twelve cells
        foreach (var cell in Cells())
        {
            var compilation = Compilations[cell.Assembly].Value;
            var run = (IMethodSymbol)compilation.GetTypeByMetadataName("Lib.Api")!.GetMembers("Run").Single();
            var rules = ModelGenerator.Reachability(compilation, run);
            var expected = Expected(cell);
            failures.AddRange(Observed(cell, compilation, rules).Where(observed => observed.Holds != expected)
                                                               .Select(observed => $"{cell}: {observed.What} is {observed.Holds}, expected {expected}"));
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.All(Enum.GetValues<Assembly>(), value => Assert.Contains(cells, cell => cell.Assembly == value));
        Assert.All(Enum.GetValues<Part>(), value => Assert.Contains(cells, cell => cell.Part == value));
    }
}
