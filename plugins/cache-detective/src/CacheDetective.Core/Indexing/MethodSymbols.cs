using Microsoft.CodeAnalysis;

namespace CacheDetective.Indexing;

/// <summary>How two method symbols are told apart across the indexing pass. The entry-point finders and the
/// call-graph walk both need one comparer and neither owns it: a finder that deduplicated implementations
/// differently from the walk that visits them would let the same method be walked twice.</summary>
internal static class MethodSymbols
{
    public static IEqualityComparer<IMethodSymbol> Comparer { get; } = new MethodSymbolComparer();

    private sealed class MethodSymbolComparer : IEqualityComparer<IMethodSymbol>
    {
        public bool Equals(IMethodSymbol? x, IMethodSymbol? y) => SymbolEqualityComparer.Default.Equals(x, y);

        public int GetHashCode(IMethodSymbol obj) => SymbolEqualityComparer.Default.GetHashCode(obj);
    }
}
