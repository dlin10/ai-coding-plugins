using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class CandidacyTests
{
    private const string ARGUMENT = "Arg_value_Call = ";

    private const string SOURCE = """
        namespace Lib
        {
            public sealed class Mutable { public object Value; }
            public struct Pair { public object Value; }
            public sealed class Box
            {
                public object Value { get; set; }
                public event System.Action Changed;
            }
            public sealed class Holder { public System.Action Run; }
            public static class Api
            {
                public static void MutableOnly(Mutable value) { }
                public static void StructOnly(Pair value) { }
                public static void ImmutableOnly(int value, string text) { }
                public static void HolderOnly(Holder value) { }
                public static void BoxOnly(Box value) { }
                public static void TaskOfDelegate(System.Threading.Tasks.Task<System.Func<int>> value) { }
                public static void ValueTaskOfHolder(System.Threading.Tasks.ValueTask<Holder> value) { }
                public static void TaskOfBox(System.Threading.Tasks.Task<Box> value) { }
                public static void TaskOfStruct(System.Threading.Tasks.Task<Pair> value) { }
                public static void TaskOfInt(System.Threading.Tasks.Task<int> value) { }
                public static void PlainTask(System.Threading.Tasks.Task value) { }
                public static void PlainValueTask(System.Threading.Tasks.ValueTask value) { }
            }
        }
        """;

    private static readonly Lazy<LibraryCompilationResult> Library = new(() =>
        LibraryCompilation.CompileTrees("Fixture.Library",
                                        [CSharpSyntaxTree.ParseText(SOURCE, path: "Fixture.Library/Library.cs")],
                                        EmittedAssemblies.RuntimeReferences, CancellationToken.None));

    [Fact]
    public void A_member_taking_only_a_mutable_argument_is_a_candidate()
    {
        Assert.NotNull(Synthesize("M:Lib.Api.MutableOnly(Lib.Mutable)").Driver);
    }

    [Fact]
    public void A_member_taking_only_a_struct_with_a_reference_field_is_a_candidate_and_lists_the_field()
    {
        var driver = Assert.IsType<Driver>(Synthesize("M:Lib.Api.StructOnly(Lib.Pair)").Driver);

        Assert.Contains("arg:value/F:Lib.Pair.Value", driver.Unseeded);
    }

    [Fact]
    public void A_member_taking_only_an_int_and_a_string_is_not_a_candidate()
    {
        Assert.Equal(GenerationReasons.NOT_A_CANDIDATE, Synthesize("M:Lib.Api.ImmutableOnly(System.Int32,System.String)").Reason);
    }

    [Fact]
    public void A_member_taking_only_a_task_of_a_delegate_is_a_candidate_handed_a_task_of_its_probe()
    {
        var driver = Assert.IsType<Driver>(Synthesize("M:Lib.Api.TaskOfDelegate(System.Threading.Tasks.Task{System.Func{System.Int32}})").Driver);

        Assert.Equal("global::System.Threading.Tasks.Task.FromResult<global::System.Func<global::System.Int32>>(L_value_0_Call())", Argument(driver));
        Assert.Contains(driver.Parameters, parameter => parameter.Name == "value" && parameter.Probes.Count > 0);
    }

    [Theory]
    [InlineData("M:Lib.Api.ValueTaskOfHolder(System.Threading.Tasks.ValueTask{Lib.Holder})", "M:Lib.Api.HolderOnly(Lib.Holder)",
                "new global::System.Threading.Tasks.ValueTask<global::Lib.Holder>({0})")]
    [InlineData("M:Lib.Api.TaskOfBox(System.Threading.Tasks.Task{Lib.Box})", "M:Lib.Api.BoxOnly(Lib.Box)",
                "global::System.Threading.Tasks.Task.FromResult<global::Lib.Box>({0})")]
    [InlineData("M:Lib.Api.TaskOfStruct(System.Threading.Tasks.Task{Lib.Pair})", "M:Lib.Api.StructOnly(Lib.Pair)",
                "global::System.Threading.Tasks.Task.FromResult<global::Lib.Pair>({0})")]
    public void A_member_taking_only_a_task_of_a_carrying_type_is_a_candidate_handed_what_the_bare_type_gets(string task, string bare, string recipe)
    {
        var bareValue = Argument(Assert.IsType<Driver>(Synthesize(bare).Driver));
        var taskValue = Argument(Assert.IsType<Driver>(Synthesize(task).Driver));

        Assert.Equal(string.Format(recipe, bareValue), taskValue);
    }

    [Theory]
    [InlineData("M:Lib.Api.TaskOfInt(System.Threading.Tasks.Task{System.Int32})")]
    [InlineData("M:Lib.Api.PlainTask(System.Threading.Tasks.Task)")]
    [InlineData("M:Lib.Api.PlainValueTask(System.Threading.Tasks.ValueTask)")]
    public void A_member_taking_only_a_task_that_carries_no_user_object_is_not_a_candidate(string id)
    {
        Assert.Equal(GenerationReasons.NOT_A_CANDIDATE, Synthesize(id).Reason);
    }

    [Theory]
    [InlineData("P:Lib.Box.Value")]
    [InlineData("E:Lib.Box.Changed")]
    [InlineData("M:Lib.Box.get_Value")]
    [InlineData("M:Lib.Box.set_Value(System.Object)")]
    [InlineData("M:Lib.Box.add_Changed(System.Action)")]
    [InlineData("M:Lib.Box.remove_Changed(System.Action)")]
    // The name and rows stay as recorded in the test baseline although the rows' meaning changed: only a P: or E: id answers
    // accessor now, and an accessor named by its M: id is generated as a method is (R6).
    public void Property_event_and_accessor_ids_are_accessor(string id)
    {
        var reason = Synthesize(id).Reason;

        if (id.StartsWith("M:", StringComparison.Ordinal))
            Assert.NotEqual(GenerationReasons.ACCESSOR, reason);
        else
            Assert.Equal(GenerationReasons.ACCESSOR, reason);
    }

    /// <summary>The value setup hands the call's parameter <c>value</c>.</summary>
    /// <param name="driver">The driver.</param>
    private static string Argument(Driver driver)
    {
        var line = driver.Source.Split('\n').Select(text => text.Trim()).Single(text => text.StartsWith(ARGUMENT, StringComparison.Ordinal));
        return line[ARGUMENT.Length..].TrimEnd(';');
    }

    private static DriverSynthesis Synthesize(string id)
    {
        var library = Library.Value;
        Assert.NotNull(library.Compilation);
        var member = Assert.IsAssignableFrom<Microsoft.CodeAnalysis.ISymbol>(DriverSynthesizer.FindMember(library.Compilation!, id));
        return DriverSynthesizer.Synthesize(library.Compilation!, member, library.ExternMembers, library.OpenedFields, CancellationToken.None);
    }
}
