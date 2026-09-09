using Microsoft.CodeAnalysis;

namespace CacheDetective.Indexing.EntryPoints;

/// <summary>One form of entry point, asked about one type. A finder appends what it recognises; the order
/// the finders are asked in is the order the entry points reach the graph.</summary>
internal interface IEntryPointFinder
{
    void Find(INamedTypeSymbol type, EntryPointContext context, ICollection<EntryPoint> entryPoints);
}
