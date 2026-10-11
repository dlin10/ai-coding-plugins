using System.Text;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class CellVocabularyTests
{
    [Fact]
    public void Writes_cells_on_a_parameter_that_is_not_an_array_is_refused() =>
        Refused("Plain", """ "effects":{"value":["writes-cells"]} """, "array");

    [Fact]
    public void Writes_cells_on_this_of_a_type_other_than_System_Array_is_refused() =>
        Refused("Self", """ "effects":{"this":["writes-cells"]} """, "System.Array");

    [Fact]
    public void Stores_without_writes_cells_is_refused() =>
        Refused("Fill", """ "effects":{},"stores":{"array":["arg:value"]} """, "writes-cells");

    [Fact]
    public void Stored_value_that_does_not_convert_to_the_element_type_is_refused() =>
        Refused("Wrong", """ "effects":{"array":["writes-cells"]},"stores":{"array":["arg:value"]} """, "does not convert to string");

    [Fact]
    public void This_on_a_static_member_is_refused() =>
        Refused("Plain", """ "effects":{},"result":"[this]" """, "this needs an instance");

    [Fact]
    public void Reads_deep_on_this_is_accepted()
    {
        // ValueTuple<…>.GetHashCode runs GetHashCode of what the receiver holds (question 80).
        var (models, rejections) = Resolve(Entry("Self", """ "effects":{"this":["reads-deep"]} """));

        Assert.Empty(rejections);
        Assert.Contains(LibraryEffect.DeepReadOf("this"), Assert.Single(models.Members, member => member.Id == Id("Self")).Effects);
    }

    [Fact]
    public void Reads_deep_on_this_of_a_static_member_is_refused() =>
        Refused("Plain", """ "effects":{"this":["reads-deep"]} """, "this needs an instance");

    [Fact]
    public void Writes_arg_on_this_is_refused() => Refused("Self", """ "effects":{"this":["writes-arg"]} """, "this");

    [Fact]
    public void Entries_differing_only_in_stores_disagree()
    {
        var (models, rejections) = Resolve(Entry("Fill", """ "effects":{"array":["writes-cells"]},"stores":{"array":["arg:value"]} """),
            Entry("Fill", """ "effects":{"array":["writes-cells"]},"stores":{"array":["elements(arg:array)"]} """));
        Assert.Equal(2, rejections.Count);
        Assert.All(rejections, rejection => Assert.Contains("disagree", rejection.Reason));
        Assert.True(Assert.Single(models.Members, member => member.Id == Id("Fill")).DeclaredOpaque);
    }

    [Fact]
    public void Built_in_entry_with_writes_cells_and_stores_loads()
    {
        var text = File(Entry("Fill", """ "effects":{"array":["writes-cells"]},"stores":{"array":["arg:value"]} """))
            .Replace("\"models\":", "\"versions\":{\"minimum\":\"1.0.0.0\",\"maximumExclusive\":\"2.0.0.0\"},\"models\":");
        var member = Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(text)).Members);
        Assert.Equal("WriteCells", Assert.Single(member.Effects).Kind.ToString());
        Assert.Equal("arg:value", Assert.Single(member.Stores["array"]).ToString());
    }

    [Theory]
    [InlineData("Sequence", "[sequence(arg:a,arg:b)]", true)]
    [InlineData("Sequence", "[sequence(arg:b,arg:a)]", true)]
    [InlineData("Dictionary", "dictionary(arg:key,sequence(arg:a,arg:b))", true)]
    [InlineData("Dictionary", "dictionary(arg:key,sequence(arg:b,arg:a))", true)]
    [InlineData("Sequence", "[sequence(arg:a,arg:bad)]", false)]
    [InlineData("Sequence", "[sequence(arg:bad,arg:a)]", false)]
    public void Every_typed_leaf_is_checked_whatever_its_order(string member, string result, bool accepted)
    {
        var (_, rejections) = Resolve(Entry(member, "\"effects\":{},\"result\":\"" + result + "\""));
        if (accepted)
            Assert.Empty(rejections);
        else
            Assert.Contains("does not convert to Vocabulary.Base", Assert.Single(rejections).Reason);
    }

    [Theory]
    [InlineData("Plain", """ "effects":{"value":["reads-deep"]},"calls":[{"member":"GetHashCode","on":"arg:value"}] """)]
    [InlineData("Fill", """ "effects":{},"calls":[{"member":"Equals","on":"elements(arg:array)","inputs":[["arg:value"]]}] """)]
    [InlineData("Self", """ "effects":{},"calls":[{"member":"CompareTo","on":"this","inputs":[["this"]]}] """)]
    public void Calls_of_a_listed_member_with_its_inputs_are_accepted(string member, string decision)
    {
        // A known call runs the user's equality, hash and ordering members where it stands (question 47).
        var (models, rejections) = Resolve(Entry(member, decision));

        Assert.Empty(rejections);
        Assert.Single(Assert.Single(models.Members, model => model.Id == Id(member)).Calls);
    }

    [Theory]
    [InlineData("Plain", """ "effects":{},"calls":[{"member":"ToString","on":"arg:value"}] """, "needs a member of")]
    [InlineData("Plain", """ "effects":{},"calls":[{"member":"Equals","on":"arg:value"}] """, "takes 1 or 2 inputs, not 0")]
    [InlineData("Plain", """ "effects":{},"calls":[{"member":"GetHashCode"}] """, "needs the objects it is on")]
    [InlineData("Plain", """ "effects":{},"calls":[{"member":"GetHashCode","on":"new"}] """, "new is not allowed")]
    [InlineData("Plain", """ "effects":{},"calls":[{"member":"GetHashCode","on":"sequence(arg:value)"}] """, "builds")]
    [InlineData("Plain", """ "effects":{},"calls":[{"member":"GetHashCode","on":"arg:missing"}] """, "names no parameter")]
    [InlineData("Plain", """ "effects":{},"calls":[{"member":"GetHashCode","on":"this"}] """, "this needs an instance")]
    [InlineData("Plain", """ "effects":{},"calls":[{"member":"GetHashCode","on":"arg:value","extra":1}] """, "unknown property")]
    public void Calls_breaking_a_rule_are_refused(string member, string decision, string reason) => Refused(member, decision, reason);

    [Fact]
    public void Entries_differing_only_in_calls_disagree()
    {
        var (models, rejections) = Resolve(Entry("Plain", """ "effects":{},"calls":[{"member":"GetHashCode","on":"arg:value"}] """),
                                           Entry("Plain", """ "effects":{},"calls":[{"member":"Equals","on":"arg:value","inputs":[["arg:value"]]}] """));

        Assert.Equal(2, rejections.Count);
        Assert.True(Assert.Single(models.Members, model => model.Id == Id("Plain")).DeclaredOpaque);
    }

    private static void Refused(string member, string decision, string reason)
    {
        var (_, rejections) = Resolve(Entry(member, decision));
        Assert.Contains(reason, Assert.Single(rejections).Reason, StringComparison.Ordinal);
    }

    private static string Id(string member) => DocumentationCommentId.CreateDeclarationId(LibrarySource.Value.GetTypeByMetadataName("Vocabulary.Lib")!.GetMembers(member).Single())!;
    private static string Entry(string member, string decision) => "{\"member\":\"" + Id(member) + "\"," + decision + "}";
    private static string File(params string[] entries) => "{\"schemaVersion\":1,\"assemblies\":[\"Vocabulary\"],\"models\":[" + string.Join(',', entries) + "]}";

    private static (LibraryModels Models, IReadOnlyList<ModelRejection> Rejections) Resolve(params string[] entries)
    {
        using var repo = new CellModelRepository(File(entries));
        using var bytes = new MemoryStream();
        Assert.True(LibrarySource.Value.Emit(bytes).Success);
        var compilation = CSharpCompilation.Create("Check", [], StubAssemblies.PlatformWithout([]).Append(MetadataReference.CreateFromImage(bytes.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var files = ProjectModelFiles.Read(repo.Root);
        return ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(repo.Root, files));
    }

    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Vocabulary", [CSharpSyntaxTree.ParseText("""
        using System;
        using System.Collections.Generic;
        namespace Vocabulary;
        public class Base { }
        public class A : Base { }
        public class B : Base { }
        public class Bad { }
        public class Lib
        {
            public static object Plain(object value) => null!;
            public object Self() => null!;
            public static void Fill(object[] array, object value) { }
            public static void Wrong(string[] array, Version value) { }
            public static IEnumerable<Base> Sequence(A a, B b, Bad bad) => null!;
            public static Dictionary<string, IEnumerable<Base>> Dictionary(string key, A a, B b, Bad bad) => null!;
        }
        """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
}
