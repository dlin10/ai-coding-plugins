using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class CandidacyTests
{
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
            public static class Api
            {
                public static void MutableOnly(Mutable value) { }
                public static void StructOnly(Pair value) { }
                public static void ImmutableOnly(int value, string text) { }
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

    private static DriverSynthesis Synthesize(string id)
    {
        var library = Library.Value;
        Assert.NotNull(library.Compilation);
        var member = Assert.IsAssignableFrom<Microsoft.CodeAnalysis.ISymbol>(DriverSynthesizer.FindMember(library.Compilation!, id));
        return DriverSynthesizer.Synthesize(library.Compilation!, member, library.ExternMembers, library.OpenedFields, CancellationToken.None);
    }
}
