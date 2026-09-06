using CacheDetective.Graph;
using CacheDetective.Rules;

namespace CacheDetective.Tests;

/// <summary>
/// What names a finding, independently of the run that produced it: the rule, the solution, the handler
/// symbol and its project, the key template and store, and the rule's own target. The target is the half
/// that differs per rule and the half that used to be dropped — a table for an unguarded write, the
/// external source for <c>EXTERNAL_NO_TTL</c>, and for <c>STALE_PARENT_KEY</c> the child key, so that the
/// parent/child pair the finding is about is named by both halves rather than by one.
/// </summary>
internal sealed record FindingIdentity(string Rule, string Solution, string Handler, string Project,
                                       string? Template, string? Store, string Target, Confidence Confidence,
                                       bool Suppressed)
{
    /// <summary>The identity without the confidence or the suppression, which are what a comparison
    /// reports <em>about</em> a finding rather than which finding it is.</summary>
    public string Key => string.Join(" | ", Rule, Solution, Handler, Project, Template ?? "-", Store ?? "-", Target);

    public override string ToString() => $"{Key} | {Confidence} | {(Suppressed ? "suppressed" : "reported")}";
}

/// <summary>
/// Every finding of every rule, named the one way. Both the behaviour snapshot and the demo metrics read
/// findings from a graph, and they named them differently until this was shared: the metrics identity
/// dropped the project and named only the child of a stale-parent pair, so two findings that differ only
/// in those collapsed into one.
/// </summary>
internal static class FindingIdentities
{
    internal static IReadOnlyList<FindingIdentity> Collect(CacheGraph graph)
    {
        var found = new List<FindingIdentity>();
        foreach (var finding in new UnguardedWriteRule().Evaluate(graph))
        {
            // The subject is the handler at the head of the chain, which the rule names: a hidden write's
            // Write.From is the procedure or the trigger that performed it, not the handler to fix.
            found.Add(Identity(finding.RuleName, finding.Handler, finding.Key.Template, finding.Key.Store,
                               finding.Table.Name, finding.Confidence, finding.Suppressed));
        }

        foreach (var finding in new ExternalNoTtlRule().Evaluate(graph))
        {
            found.Add(Identity(ExternalNoTtlFinding.Rule, finding.Handler, finding.Key.Template, finding.Key.Store,
                               Describe(finding.Source), finding.Confidence, finding.Suppressed));
        }

        foreach (var finding in new StaleParentKeyRule().Evaluate(graph))
        {
            found.Add(Identity(StaleParentKeyFinding.Rule, finding.Handler, finding.Parent.Template,
                               finding.Parent.Store, Describe(finding.Child), finding.Confidence, false));
        }

        var invalidations = new OrphanInvalidationRule().Evaluate(graph);
        foreach (var finding in invalidations.Orphans)
        {
            var removed = (CacheKey)finding.Invalidation.To;
            found.Add(Identity(OrphanInvalidationFinding.Rule, (Handler)finding.Invalidation.From, removed.Template,
                               removed.Store, Describe(removed), finding.Invalidation.Confidence, false));
        }

        foreach (var finding in invalidations.PatternMismatches)
        {
            // The key slot carries the key that is cached; the target, the template the handler removes —
            // the two being different is the whole finding.
            found.Add(Identity(PatternMismatchFinding.Rule, (Handler)finding.Invalidation.From,
                               finding.CachedKey.Template, finding.CachedKey.Store,
                               Describe((CacheKey)finding.Invalidation.To), finding.Invalidation.Confidence, false));
        }

        return found;
    }

    internal static string Describe(CacheKey key) => $"{key.Template}@{key.Store}";

    internal static string Describe(ExternalSource source) =>
        $"external:{source.Kind}:{source.Owner}:{source.ClientName ?? "-"}:{source.Method} {source.Template}";

    private static FindingIdentity Identity(string rule, Handler handler, string? template, string? store, string target,
                                            Confidence confidence, bool suppressed) =>
        new(rule, handler.Solution, handler.Symbol, handler.Project ?? "-", template, store, target, confidence, suppressed);
}
