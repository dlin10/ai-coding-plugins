using CacheDetective.Caching;
using CacheDetective.Events;

namespace CacheDetective.Indexing;

public sealed record IndexerOptions(IReadOnlyList<CacheRecognizer> CacheRecognizers,
                                    IReadOnlyList<EventRecognizer> EventRecognizers);
