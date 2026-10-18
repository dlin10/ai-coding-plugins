using System.Text.Json;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class DeclarationIdGrammarTests
{
    // For a function pointer type the call writes nothing — `Open(A,,B)`, `Initialize()`, `Invoke(A)~` — which is the text the
    // grammar must refuse, so the id of a member whose signature has one names no member and is kept apart.
    private static readonly Lazy<(IReadOnlyList<string> Ids, IReadOnlyList<string> FunctionPointerIds)> Fixture = new(() =>
    {
        var references = StubAssemblies.PlatformWithout([])
                                       .Concat(StubAssemblies.Names.Select(name => StubAssemblies.Get(name, StubAssemblies.DefaultVersion(name))))
                                       .Append(StubAssemblies.Get(StubAssemblies.NEWTONSOFT_JSON, 12))
                                       .ToArray();
        var compilation = CSharpCompilation.Create("Ids", [], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var ids = new List<string>();
        var functionPointerIds = new List<string>();
        void Walk(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
            {
                if (member is INamespaceOrTypeSymbol nested)
                {
                    if (nested is INamedTypeSymbol type)
                        ids.Add(DocumentationCommentId.CreateDeclarationId(type)!);
                    Walk(nested);
                }
                else if (member is IMethodSymbol method && DocumentationCommentId.CreateDeclarationId(method) is { } id)
                    (FunctionPointers.In(method) ? functionPointerIds : ids).Add(id);
            }
        }
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            Walk(assembly.GlobalNamespace);
        return (ids, functionPointerIds);
    });

    private static readonly Lazy<IReadOnlyList<string>> FixtureIds = new(() => Fixture.Value.Ids);

    [Fact]
    public void Every_declaration_id_of_the_fixture_references_parses()
    {
        Assert.True(FixtureIds.Value.Count >= 20000, $"only {FixtureIds.Value.Count} ids");
        var refused = FixtureIds.Value.Where(id => DeclarationId.Parse(id) is null).ToArray();
        Assert.Empty(refused);
    }

    [Fact]
    public void Parsed_id_renders_back_to_itself()
    {
        var changed = FixtureIds.Value.Where(id => DeclarationId.Parse(id)?.ToString() != id).ToArray();
        Assert.Empty(changed);
    }

    [Fact]
    public void Every_built_in_model_id_parses()
    {
        var assembly = typeof(LibraryModels).Assembly;
        var ids = new List<string>();
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("LibraryModels.BuiltIn.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var document = JsonDocument.Parse(stream);
            ids.AddRange(document.RootElement.GetProperty("models").EnumerateArray().Select(entry => entry.GetProperty("member").GetString()!));
            if (document.RootElement.TryGetProperty("immutableTypes", out var types))
                ids.AddRange(types.EnumerateArray().Select(entry => entry.GetProperty("type").GetString()!));
        }

        Assert.Contains(ids, id => id.StartsWith("M:", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.StartsWith("T:", StringComparison.Ordinal));
        Assert.All(ids, id => Assert.Equal(id, DeclarationId.Parse(id)?.ToString()));
    }

    [Fact]
    public void Pattern_names_its_type_and_member()
    {
        var pattern = DeclarationId.Parse("M:A.B`1.Run(*)");

        Assert.NotNull(pattern);
        Assert.True(pattern.IsPattern);
        Assert.False(pattern.IsMember);
        Assert.Equal("A.B`1", pattern.TypeName);
        Assert.Equal("Run", pattern.Member!.Name);
        Assert.True(ProjectModelFiles.IsPattern("M:A.B`1.Run(*)"));
    }

    [Theory]
    [InlineData("M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``1}")]
    [InlineData("M:A.Outer`1.Inner.Run(`0)")]
    [InlineData("T:A.Outer`1.Inner`2")]
    [InlineData("M:A.B.Run(System.Int32[][])")]
    [InlineData("M:A.B.Run(System.Int32[0:,0:])")]
    [InlineData("M:A.B.Run(System.Int32@)")]
    [InlineData("M:A.B.Run(System.Byte*,System.Int32)")]
    [InlineData("M:A.B.Run(System.Nullable{System.Int32})")]
    [InlineData("M:A.B.Run(System.ValueTuple{System.Int32,System.String})")]
    [InlineData("M:A.B.op_Implicit(A.B)~System.Int32")]
    [InlineData("M:A.B.#ctor(System.Int32)")]
    [InlineData("M:A.B.#cctor")]
    [InlineData("M:A.B.get_Name")]
    [InlineData("M:A.B.Run``1(System.Collections.Generic.List{System.Collections.Generic.KeyValuePair{``0,System.String}})")]
    [InlineData("M:A.B.Run(*)")]
    [InlineData("M:A.Outer`1.Inner.Run(A.Outer`1.Inner`0)")]
    [InlineData("M:A.B`1.System#Collections#Generic#IDictionary<TKey,TValue>#Add(`0)")]
    [InlineData("M:A.B.global::A#IRun#Run")]
    [InlineData("M:A.B.<Run>g__Local|0_0(System.Int32)~System.Int32")]
    [InlineData("T:<PrivateImplementationDetails>.__StaticArrayInitTypeSize=24")]
    public void Accepted_declaration_ids(string id) => Assert.Equal(id, DeclarationId.Parse(id)?.ToString());

    [Theory]
    [InlineData("M:A.B.Run(A-B)", "member")]
    [InlineData("M:A.B.Run~A-B", "member")]
    [InlineData("M:A.B.Run!", "member")]
    [InlineData("M:A.B.Run!(*)", "pattern")]
    [InlineData("M:A.B.Run(A,,B)", "member")]
    [InlineData("M:A.B.Run((A))", "member")]
    [InlineData("M:A.B.Run(A{)", "member")]
    [InlineData("M:A.B.Run(A})", "member")]
    [InlineData("M:A.B.Run(A{})", "member")]
    [InlineData("M:A.B.Run(``)", "member")]
    [InlineData("M:A.B.Run(`)", "member")]
    [InlineData("M:A.B.Run(A[)", "member")]
    [InlineData("M:A.B.Run(A[0:,)", "member")]
    [InlineData("M:A.B.Run~A~B", "member")]
    [InlineData("M:1A.B.Run", "member")]
    [InlineData("M:A..B.Run", "member")]
    [InlineData("M:A.B.", "member")]
    [InlineData("M:A.B.Run (A)", "member")]
    [InlineData("T:A.B.Run(A)", "member")]
    [InlineData("T:A.B.Run(A)", "type")]
    [InlineData("M:A.B.Run(*)", "member")]
    [InlineData("M:A.B.Run(*,A)", "pattern")]
    [InlineData("", "member")]
    [InlineData("T:A-B", "type")]
    [InlineData("T:A.B=C", "type")]
    [InlineData("M:A.B.Run()", "member")]
    [InlineData("M:A.B.Run``1(*)", "pattern")]
    // A name nested in a compiler-generated one is still held to the grammar (review F-0056).
    [InlineData("T:<PrivateImplementationDetails>.A-B", "type")]
    [InlineData("M:<PrivateImplementationDetails>.A-B.Run", "member")]
    public void Refused_declaration_ids(string id, string expected)
    {
        Assert.False(expected switch
        {
            "member" => BuiltInModelReader.IsMemberId(id),
            "pattern" => ProjectModelFiles.IsPattern(id),
            _ => DeclarationId.Parse(id) is { IsType: true }
        });
    }

    [Fact]
    public void The_nine_skipped_fixture_members_carry_a_function_pointer()
    {
        var skipped = Fixture.Value.FunctionPointerIds;

        Assert.Equal(9, skipped.Count);
        // The text the call writes for each is the text the grammar refuses and the text rule marks.
        Assert.All(skipped, id => Assert.False(DeclarationId.Parse(id) is { IsMember: true }, id));
        Assert.All(skipped, id => Assert.True(DeclarationId.HasEmptyTypeSegment(id), id));
        Assert.DoesNotContain(FixtureIds.Value, id => DeclarationId.HasEmptyTypeSegment(id));
    }

    [Theory]
    [InlineData("M:A.B.Run(A,,B)", true)]
    [InlineData("M:A.B.Run(,B)", true)]
    [InlineData("M:A.B.Run(A,)", true)]
    [InlineData("M:A.B.Initialize()", true)]
    [InlineData("M:P.A([])", true)]
    [InlineData("M:P.A([,],A)", true)]
    [InlineData("M:P.B(*)", true)]
    [InlineData("M:P.B(*,A)", true)]
    [InlineData("M:P.C(@)", true)]
    [InlineData("M:A.B.Invoke(A)~", true)]
    [InlineData("M:P.D~[]", true)]
    [InlineData("M:P.E~*", true)]
    [InlineData("M:A.B.Run(A", false)]
    [InlineData("M:A.B.Run(", false)]
    [InlineData("M:A..B()", false)]
    [InlineData("M:A.B.Run", false)]
    [InlineData("M:A.B.Run(A[])", false)]
    [InlineData("M:A.B.Run(A)~A.C", false)]
    [InlineData("M:A.B.Run(A-B,)", false)]
    [InlineData("M:A.B.Run(A{,})", false)]
    [InlineData("M:A.B.Run(A{*})", false)]
    [InlineData("M:A.B.Run(.A)", false)]
    [InlineData("M:A.B.Run~A-B", false)]
    [InlineData("T:A.B", false)]
    [InlineData("", false)]
    public void Empty_type_segments_are_marked_and_other_malformed_ids_are_not(string id, bool marked) =>
        Assert.Equal(marked, DeclarationId.HasEmptyTypeSegment(id));

    [Fact]
    public void A_project_file_still_reads_any_parameters_as_a_pattern()
    {
        // The text rule marks `(*)`, and the readers of built-in and project entries keep reading it as the pattern.
        Assert.True(DeclarationId.HasEmptyTypeSegment("M:P.B(*)"));
        Assert.True(ProjectModelFiles.IsPattern("M:P.B(*)"));
        Assert.True(DeclarationId.Parse("M:P.B(*)")!.IsPattern);
    }
}
