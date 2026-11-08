using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>The shared frameworks' census among the built-in models. The census files, the built-in files in a framework's folder, are
/// data the owner's regeneration writes beside the topical files the owner writes by hand: a test of the owner's table leaves their
/// records out, and a test of an invariant every record keeps finds a census member in the frameworks' own compilations.</summary>
internal static class BuiltInCensus
{
    // One read of the installed frameworks for the whole run: it opens every assembly of both.
    private static readonly Lazy<Compilation[]> Frameworks = new(() =>
    {
        var frameworks = FrameworkAssemblies.Installed(ImplementationAssemblies.ForThisProcess().DotnetRoot);
        return [Compile("FrameworkReference", frameworks.ReferenceFiles), Compile("FrameworkImplementation", frameworks.Files)];
    });

    /// <summary>The member ids of the census files' records: their models and their failed members.</summary>
    internal static IReadOnlySet<string> Ids { get; } = ReadIds();

    /// <summary>Whether an embedded resource is a census file: a built-in file in the folder of one of the frameworks.</summary>
    /// <param name="resourceName">The resource's name.</param>
    internal static bool IsCensusFile(string resourceName) =>
        FrameworkAssemblies.FRAMEWORKS.Any(framework => resourceName.StartsWith(LibraryModels.BUILT_IN_RESOURCE_PREFIX + framework, StringComparison.Ordinal));

    /// <summary>Looks a built-in record's member up: in <paramref name="fixture"/>, where its id names the symbols, unless the record is a
    /// census record whose id names nothing there; then in the first of the frameworks' compilations, their reference assemblies before
    /// their implementation, that holds a method of the record's assemblies with that id, as the census names each member in one of the
    /// two. A framework assembly can carry an internal copy of another's type, which makes such an id ambiguous there, so the return type
    /// is left out of the search and each candidate's own id is compared.</summary>
    /// <param name="record">The built-in record.</param>
    /// <param name="fixture">The compilation the test looks members up in.</param>
    internal static MemberLookup Lookup(LibraryModel record, Compilation fixture)
    {
        var named = DocumentationCommentId.GetSymbolsForDeclarationId(record.Id, fixture);
        if (!named.IsEmpty || !Ids.Contains(record.Id))
            return new MemberLookup(fixture, named);
        var returnType = record.Id.IndexOf('~');
        var search = returnType < 0 ? record.Id : record.Id[..returnType];
        foreach (var compilation in Frameworks.Value)
        {
            ISymbol[] members = [.. DocumentationCommentId.GetSymbolsForDeclarationId(search, compilation)
                                                          .Where(symbol => record.Assemblies.Any(range => range.AssemblyName == symbol.ContainingAssembly.Identity.Name) &&
                                                                           DocumentationCommentId.CreateDeclarationId(symbol) == record.Id)];
            if (members.Length != 0)
                return new MemberLookup(compilation, members);
        }

        return new MemberLookup(fixture, named);
    }

    /// <summary>The member ids of the records of every embedded census file.</summary>
    private static HashSet<string> ReadIds()
    {
        var assembly = typeof(LibraryModels).Assembly;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(IsCensusFile))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            ids.UnionWith(BuiltInModelReader.Read(bytes.ToArray()).Members.Select(member => member.Id));
        }

        return ids;
    }

    /// <summary>A compilation that references the given assembly files and has no source.</summary>
    /// <param name="name">The compilation's name.</param>
    /// <param name="files">The assembly files.</param>
    private static CSharpCompilation Compile(string name, IEnumerable<string> files) =>
        CSharpCompilation.Create(name, references: files.Select(file => MetadataReference.CreateFromFile(file)));

    /// <summary>Where a built-in record's member was looked up, and what was found.</summary>
    /// <param name="Compilation">The compilation it was looked up in.</param>
    /// <param name="Symbols">The symbols found there.</param>
    internal sealed record MemberLookup(Compilation Compilation, IReadOnlyList<ISymbol> Symbols);
}
