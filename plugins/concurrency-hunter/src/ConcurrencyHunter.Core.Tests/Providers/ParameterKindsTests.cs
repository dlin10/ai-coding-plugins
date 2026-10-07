using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>How the driver fills a parameter: the first of G-3's seven axes that matches, after <c>ref</c>, <c>out</c> and
/// <c>in</c> are replaced by the referenced type.</summary>
public sealed class ParameterKindsTests
{
    private const string SOURCE = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace Lib
        {
            public interface ISink { void Put(object value); }
            public abstract class Handler { protected Handler() { } public abstract void Handle(object value); }
            public abstract class Closed { internal Closed() { } public abstract void Handle(object value); }
            public abstract class Secret { protected Secret() { } internal abstract void Hide(); }
            public class Bag { public object Item; public virtual void Add(object item) { Item = item; } }
            public class Guarded { internal Guarded() { } public virtual void Add(object item) { } }
            public sealed class Box { public Box(Func<int> make) { } }
            public struct Pair { public string Name; public object Value; }
            public struct Point { public int X; public int Y; }
            public sealed class Many : List<int>, ISink { public void Put(object value) { } }
            public sealed class Holder { public Action Run; }

            public static unsafe class Api
            {
                public static void Axes(Span<int> span, int* pointer, Func<int, bool> predicate, string text, DateTime when, Point point, int[] numbers,
                                        IEnumerable<int> sequence, IReadOnlyList<Bag> list, object item, ISink sink, Handler handler, Bag bag, Box box,
                                        Pair pair, Task task, Task<int> result, Closed closed, Secret secret, Guarded guarded, Many many) { }
                public static void Generic<T>(T value, IEnumerable<T> values) { }
                public static void Passing(ref Action first, out Action second, in Action third, ref Bag fourth) { second = null; }
                public static void Arrays(int[] single, int[,] square, int[][] jagged, Func<int>[] probes, Action done) { }
                public static void Tasks(Task<Func<int>> probe, ValueTask<Holder> holder, Task<Box> box, Task<Pair> pair, Task<int> number, Task plain,
                                         ValueTask plainValue, Task<Task<Box>> nested) { }
            }
        }
        """;

    private static readonly Lazy<LibraryCompilationResult> Library = new(() =>
        LibraryCompilation.CompileTrees("Fixture.Library",
                                        [CSharpSyntaxTree.ParseText(SOURCE, new CSharpParseOptions(LanguageVersion.Preview), "Fixture.Library/Library.cs")],
                                        EmittedAssemblies.RuntimeReferences, CancellationToken.None));

    [Fact]
    public void Axis_1_a_span_or_a_pointer_is_not_synthesized()
    {
        Assert.Equal(ParameterKind.NotSynthesized, Kind("Axes", "span"));
        Assert.Equal(ParameterKind.NotSynthesized, Kind("Axes", "pointer"));
    }

    [Fact]
    public void Axis_2_a_delegate_is_a_delegate()
    {
        Assert.Equal(ParameterKind.Delegate, Kind("Axes", "predicate"));
        Assert.False(ParameterKinds.IsHolding(ParameterKind.Delegate));
    }

    [Fact]
    public void Axis_3_an_immutable_type_or_a_plain_struct_is_canned_and_not_holding()
    {
        Assert.Equal(ParameterKind.Canned, Kind("Axes", "text"));
        Assert.Equal(ParameterKind.Canned, Kind("Axes", "when"));
        Assert.Equal(ParameterKind.Canned, Kind("Axes", "point"));
        Assert.False(ParameterKinds.IsHolding(ParameterKind.Canned));
    }

    [Fact]
    public void Axis_4_an_array_or_a_sequence_interface_is_a_holding_container()
    {
        Assert.Equal(ParameterKind.Container, Kind("Axes", "numbers"));
        Assert.Equal(ParameterKind.Container, Kind("Axes", "sequence"));
        Assert.Equal(ParameterKind.Container, Kind("Axes", "list"));
        Assert.True(ParameterKinds.IsHolding(ParameterKind.Container));
    }

    [Fact]
    public void IEnumerable_of_T_takes_axis_4_before_axis_5()
    {
        var sequence = Parameter("Axes", "sequence").Type;

        Assert.Equal(TypeKind.Interface, sequence.TypeKind);
        Assert.Equal(ParameterKind.Container, ParameterKinds.Of(sequence));
        Assert.Equal(ParameterKind.Container, Kind("Generic", "values"));
        // A class implementing a sequence interface is not a sequence interface: it takes its own axis.
        Assert.Equal(ParameterKind.RecipeValue, Kind("Axes", "many"));
    }

    [Fact]
    public void Axis_5_object_an_interface_a_derivable_abstract_class_or_a_type_parameter_is_a_probe_object()
    {
        Assert.Equal(ParameterKind.ProbeObject, Kind("Axes", "item"));
        Assert.Equal(ParameterKind.ProbeObject, Kind("Axes", "sink"));
        Assert.Equal(ParameterKind.ProbeObject, Kind("Axes", "handler"));
        Assert.Equal(ParameterKind.ProbeObject, Kind("Generic", "value"));
        Assert.True(ParameterKinds.IsHolding(ParameterKind.ProbeObject));
    }

    [Fact]
    public void Axis_6_a_non_sealed_concrete_class_a_user_could_subclass_is_a_subclass()
    {
        Assert.Equal(ParameterKind.Subclass, Kind("Axes", "bag"));
        Assert.True(ParameterKinds.IsHolding(ParameterKind.Subclass));
    }

    [Fact]
    public void Axis_7_a_sealed_class_a_task_or_a_struct_with_references_is_the_recipes_value()
    {
        Assert.Equal(ParameterKind.RecipeValue, Kind("Axes", "box"));
        Assert.Equal(ParameterKind.RecipeValue, Kind("Axes", "pair"));
        Assert.Equal(ParameterKind.RecipeValue, Kind("Axes", "task"));
        Assert.Equal(ParameterKind.RecipeValue, Kind("Axes", "result"));
        Assert.True(ParameterKinds.IsHolding(ParameterKind.RecipeValue));
    }

    [Fact]
    public void A_class_no_user_could_subclass_falls_to_axis_7()
    {
        // No accessible constructor, an abstract member no other assembly can override, a constructor only the library sees.
        Assert.Equal(ParameterKind.RecipeValue, Kind("Axes", "closed"));
        Assert.Equal(ParameterKind.RecipeValue, Kind("Axes", "secret"));
        Assert.Equal(ParameterKind.RecipeValue, Kind("Axes", "guarded"));
    }

    [Fact]
    public void Ref_out_and_in_parameters_are_asked_about_with_their_referenced_type()
    {
        Assert.Equal(RefKind.Ref, Parameter("Passing", "first").RefKind);
        Assert.Equal(ParameterKind.Delegate, Kind("Passing", "first"));
        Assert.Equal(ParameterKind.Delegate, Kind("Passing", "second"));
        Assert.Equal(ParameterKind.Delegate, Kind("Passing", "third"));
        Assert.Equal(ParameterKind.Subclass, Kind("Passing", "fourth"));
    }

    [Fact]
    public void A_task_of_T_is_a_candidate_exactly_when_T_is()
    {
        // A delegate, a class holding a delegate, a sealed class and a struct with a reference field each carry a user object, and so
        // does the task completing with one, at any depth (R6).
        foreach (var name in new[] { "probe", "holder", "box", "pair", "nested" })
        {
            var type = Parameter("Tasks", name).Type;
            Assert.True(ParameterKinds.IsCandidate(type), $"{type.ToDisplayString()} is no candidate");
            Assert.True(ParameterKinds.IsCandidate(((INamedTypeSymbol)type).TypeArguments[0]));
            Assert.Equal(ParameterKind.RecipeValue, ParameterKinds.Of(type));
        }

        // A task of an int, and a task completing with no value, carry nothing.
        foreach (var name in new[] { "number", "plain", "plainValue" })
            Assert.False(ParameterKinds.IsCandidate(Parameter("Tasks", name).Type), $"{name} is a candidate");
    }

    [Fact]
    public void A_rank_1_array_is_filled_with_two_values()
    {
        var setup = Setup();

        Assert.Contains("Arg_single_Call = new global::System.Int32[] { default(global::System.Int32), default(global::System.Int32) };", setup);
        Assert.Contains("Arg_probes_Call = new global::System.Func<global::System.Int32>[] { L_probes_0_Call(), L_probes_1_Call() };", setup);
    }

    [Fact]
    public void A_rank_2_array_has_every_dimension_of_length_1_but_the_last_of_length_2()
    {
        Assert.Contains("Arg_square_Call = new global::System.Int32[,] { { default(global::System.Int32), default(global::System.Int32) } };", Setup());
    }

    [Fact]
    public void A_jagged_array_holds_two_arrays_of_the_same_rule()
    {
        Assert.Contains("Arg_jagged_Call = new global::System.Int32[][] { new global::System.Int32[] { default(global::System.Int32), default(global::System.Int32) }, " +
                        "new global::System.Int32[] { default(global::System.Int32), default(global::System.Int32) } };", Setup());
    }

    private static string Setup()
    {
        var library = Library.Value.Compilation!;
        var synthesis = DriverSynthesizer.Synthesize(library, DriverSynthesizer.FindMember(library, "M:Lib.Api.Arrays(System.Int32[],System.Int32[0:,0:],System.Int32[][],System.Func{System.Int32}[],System.Action)")!,
                                                     Library.Value.ExternMembers, CancellationToken.None);
        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        return synthesis.Driver.Source;
    }

    private static ParameterKind Kind(string method, string parameter) => ParameterKinds.Of(Parameter(method, parameter).Type);

    private static IParameterSymbol Parameter(string method, string parameter)
    {
        Assert.True(Library.Value.Reason is null, $"{Library.Value.Reason}: {string.Join(", ", Library.Value.Errors)}");
        return Library.Value.Compilation!.GetTypeByMetadataName("Lib.Api")!.GetMembers(method).OfType<IMethodSymbol>().Single()
                      .Parameters.Single(candidate => candidate.Name == parameter);
    }
}
