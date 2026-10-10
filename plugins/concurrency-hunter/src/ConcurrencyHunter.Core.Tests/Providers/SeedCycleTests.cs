using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The unseeded-path walk below a field the driver cannot seed ends on every type graph: a path stops at a type it is
/// already below, across a struct hop too, and at a type whose type arguments nest more than 16 levels deep.</summary>
public sealed class SeedCycleTests
{
    private const string ROOT = "arg:value/F:Lib.Holder._root";

    [Fact]
    public void A_class_reached_again_through_a_struct_field_stops_there()
    {
        var unseeded = Unseeded("public sealed class Node { public object Item; public Pair Pair; } " +
                                "public struct Pair { public Node Back; public object Tag; }", "Node");

        Assert.Equal([ROOT + "/F:Lib.Node.Item", ROOT + "/F:Lib.Node.Pair/F:Lib.Pair.Tag"], unseeded);
    }

    [Fact]
    public void A_cycle_through_two_nested_structs_stops_at_the_class()
    {
        var unseeded = Unseeded("public sealed class Node { public object Item; public Outer Outer; } " +
                                "public struct Outer { public Inner Inner; } " +
                                "public struct Inner { public Node Back; public object Tag; }", "Node");

        Assert.Equal([ROOT + "/F:Lib.Node.Item", ROOT + "/F:Lib.Node.Outer/F:Lib.Outer.Inner/F:Lib.Inner.Tag"], unseeded);
    }

    [Fact]
    public void A_growing_generic_cycle_through_an_array_stops_past_16_levels_and_records_the_field_that_leads_there()
    {
        var unseeded = Unseeded("public sealed class Node<T> { public object Item; public Node<List<T>>[] Next; }", "Node<int>");

        // Node<int> nests one level, the element below k Next fields k + 1 and its array k + 2: the array below 15 fields
        // nests 17 levels, so the walk records that field and goes no deeper.
        var expected = Enumerable.Range(0, 15).Select(depth => ROOT + Next(depth) + $"/F:{Node(depth)}.Item")
                                 .Append(ROOT + Next(15))
                                 .Order(StringComparer.Ordinal);
        Assert.Equal(expected, unseeded);

        static string Node(int lists)
        {
            var argument = "System.Int32";
            for (var level = 0; level < lists; level++)
                argument = "System.Collections.Generic.List{" + argument + "}";
            return "Lib.Node{" + argument + "}";
        }

        static string Next(int count) => string.Concat(Enumerable.Range(0, count).Select(level => $"/F:{Node(level)}.Next"));
    }

    [Fact]
    public void A_shrinking_generic_path_is_recorded_in_full()
    {
        var unseeded = Unseeded("public sealed class Leaf { public object Item; } " +
                                "public sealed class Node<T> { public object Item; public T Value; }", "Node<Node<Leaf>>");

        Assert.Equal([ROOT + "/F:Lib.Node{Lib.Node{Lib.Leaf}}.Item",
                      ROOT + "/F:Lib.Node{Lib.Node{Lib.Leaf}}.Value/F:Lib.Node{Lib.Leaf}.Item",
                      ROOT + "/F:Lib.Node{Lib.Node{Lib.Leaf}}.Value/F:Lib.Node{Lib.Leaf}.Value/F:Lib.Leaf.Item"], unseeded);
    }

    [Theory]
    [InlineData("Leaf[]")]
    [InlineData("List<Leaf>")]
    public void A_container_a_neighbour_left_behind_is_recorded_again_below_a_struct_field(string container)
    {
        var unseeded = Unseeded("public sealed class Leaf { public object Item; } " +
                                $"public sealed class Node {{ public {container} Items; public Pair Pair; }} " +
                                $"public struct Pair {{ public {container} Items; }}", "Node");

        Assert.Equal([ROOT + "/F:Lib.Node.Items/F:Lib.Leaf.Item", ROOT + "/F:Lib.Node.Pair/F:Lib.Pair.Items/F:Lib.Leaf.Item"], unseeded);
    }

    [Fact]
    public void A_struct_argument_whose_class_holds_the_struct_again_stops_at_the_class()
    {
        var library = Compile("public sealed class Node { public object Item; public Pair Pair; } " +
                              "public struct Pair { public Node Back; } " +
                              "public static class Api { public static void Run(Pair value) { } }");

        Assert.Equal(["arg:value/F:Lib.Pair.Back/F:Lib.Node.Item"],
                     Drive("M:Lib.Api.Run(Lib.Pair)", library).Unseeded.Where(path => path.StartsWith("arg:value/", StringComparison.Ordinal)));
    }

    /// <summary>The unseeded paths below a private field of <paramref name="rootType"/>, which the driver cannot read, so the
    /// whole graph below it is walked as unseeded.</summary>
    /// <param name="declarations">The library's types.</param>
    /// <param name="rootType">The private field's type.</param>
    private static string[] Unseeded(string declarations, string rootType)
    {
        var library = Compile(declarations + $" public sealed class Holder {{ private {rootType} _root; }} " +
                              "public static class Api { public static void Run(Holder value) { } }");

        return Drive("M:Lib.Api.Run(Lib.Holder)", library).Unseeded.Where(path => path.StartsWith(ROOT, StringComparison.Ordinal)).ToArray();
    }

    private static Driver Drive(string id, LibraryCompilationResult library)
    {
        Assert.NotNull(library.Compilation);
        var trace = ModelGenerator.Trace(new GenerationRequest("Fixture.Library", "1.0", id, null, null), library, CancellationToken.None);
        Assert.True(trace.Driver is not null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return trace.Driver;
    }

    private static LibraryCompilationResult Compile(string declarations) =>
        LibraryCompilation.CompileTrees("Fixture.Library",
                                        [CSharpSyntaxTree.ParseText("using System.Collections.Generic; namespace Lib { " + declarations + " }",
                                                                    new CSharpParseOptions(LanguageVersion.Preview), "Fixture.Library/Library.cs")],
                                        EmittedAssemblies.RuntimeReferences, CancellationToken.None, openFields: false);
}
