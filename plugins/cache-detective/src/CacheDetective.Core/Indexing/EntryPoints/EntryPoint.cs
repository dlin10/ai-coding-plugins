using Microsoft.CodeAnalysis;
using CacheDetective.Graph;

namespace CacheDetective.Indexing.EntryPoints;

/// <summary>A method the walk starts from, the kind of entry it is, and the routes that reach it.</summary>
internal sealed record EntryPoint(IMethodSymbol Method, string Kind, IReadOnlyList<HandlerRoute> Routes);
