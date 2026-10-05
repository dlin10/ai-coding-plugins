using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class EntryRejectionTests
{
    private const string ALL = "M:System.Linq.Enumerable.All``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Boolean";
    private const string ANY = "M:System.Linq.Enumerable.Any``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Boolean";

    [Fact]
    public void Built_in_entry_passes_against_the_compilation_that_declares_its_member()
    {
        var (entry, member, compilation) = AllEntry();

        Assert.Null(ProjectModelResolver.EntryRejection(entry, member, compilation));
    }

    [Fact]
    public void Entry_whose_versions_exclude_the_declaring_assembly_is_rejected()
    {
        var (entry, member, compilation) = AllEntry();

        Assert.Equal("assembly version is outside the entry's versions.",
                     ProjectModelResolver.EntryRejection(entry with
                     {
                         Versions = (new Version(9, 0, 0, 0), new Version(10, 0, 0, 0))
                     }, member, compilation));
    }

    [Fact]
    public void Entry_naming_another_member_is_rejected()
    {
        var (entry, member, compilation) = AllEntry();

        Assert.Equal("member does not name the definition.",
                     ProjectModelResolver.EntryRejection(entry with { Member = ANY }, member, compilation));
    }

    [Fact]
    public void Entry_whose_assemblies_exclude_the_declaring_assembly_is_rejected()
    {
        var (entry, member, compilation) = AllEntry();

        Assert.Equal("entry assemblies do not name the declaring assembly.",
                     ProjectModelResolver.EntryRejection(entry with { Assemblies = ["Other"] }, member, compilation));
    }

    [Fact]
    public void Value_typed_new_input_is_rejected_by_the_member_step()
    {
        var library = Compile("""
            [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
            namespace Lib { public static class Api { public static void Run(System.Action<int> callback) { } } }
            """);
        var compilation = Assert.IsType<Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(library.Compilation);
        var member = compilation.GetTypeByMetadataName("Lib.Api")!.GetMembers("Run").OfType<IMethodSymbol>().Single();
        var model = new LibraryModel(member.GetDocumentationCommentId()!,
            [new SupportedAssemblyVersion(ASSEMBLY, new Version(1, 0, 0, 0), new Version(2, 0, 0, 0))], [])
        {
            Fates = [new LibraryFate("callback", LibraryFateKind.InvokeNow, null, [[new NewValue()]])]
        };
        var entry = Assert.Single(ProjectModelFiles.Read("model.json", ModelEntryWriter.File(model)).Entries);

        Assert.Equal("new input needs a reference-typed parameter.",
                     ProjectModelResolver.EntryRejection(entry, member, compilation));
    }

    private static (ProjectModelEntry Entry, IMethodSymbol Member, Compilation Compilation) AllEntry()
    {
        var source = """
            [assembly: System.Reflection.AssemblyVersion("8.0.0.0")]
            namespace System.Linq
            {
                public static class Enumerable
                {
                    public static bool All<TSource>(System.Collections.Generic.IEnumerable<TSource> source,
                                                    System.Func<TSource, bool> predicate)
                    {
                        foreach (var item in source) if (!predicate(item)) return false;
                        return true;
                    }
                }
            }
            """;
        var library = Compile(source, "System.Linq");
        var compilation = Assert.IsType<Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(library.Compilation);
        var member = Assert.IsAssignableFrom<IMethodSymbol>(DriverSynthesizer.FindMember(compilation, ALL));
        var model = Assert.Single(LibraryModels.BuiltIn.Members, candidate => candidate.Id == ALL);
        var entry = Assert.Single(ProjectModelFiles.Read("model.json", ModelEntryWriter.File(model)).Entries);
        return (entry, member, compilation);
    }
}
