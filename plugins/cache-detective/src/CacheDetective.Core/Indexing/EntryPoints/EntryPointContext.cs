using CacheDetective.Graph;

namespace CacheDetective.Indexing.EntryPoints;

/// <summary>What a finder is given besides the type it is looking at. The graph arrives here as a call
/// argument rather than as constructor state for the same reason the session's collaborators take theirs as
/// arguments: a finder is built once and asked about many types, across more than one graph.</summary>
internal sealed record EntryPointContext(CacheGraph Graph, string SolutionName);
