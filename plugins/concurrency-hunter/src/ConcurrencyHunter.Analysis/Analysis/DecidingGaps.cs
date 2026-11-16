using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;

namespace ConcurrencyHunter.Analysis;

/// <summary>The single owner of the deciding gaps listed in a scope's coverage.</summary>
public static class DecidingGaps
{
    /// <summary>Joins the scope's semantic gaps with the checks of its folded findings, retaining every occurrence's decisions.</summary>
    /// <param name="scope">The scope's coverage before deciding gaps are attached.</param>
    /// <param name="findings">The folded findings of the analysis.</param>
    /// <param name="sources">The coverage places of the scope's gap call operations.</param>
    public static IReadOnlyList<DecidingGap> Build(ScopeCoverage scope, IReadOnlyList<Finding> findings,
                                                  IReadOnlyDictionary<OperationSite, CoveragePlace> sources)
    {
        var decisions = findings.Where(finding => finding.Resource.Scope == scope.ScopeId)
                                .SelectMany(finding => finding.GapChecks.Select(pair => (pair.Key, finding.Fingerprint, DecidedChecks: pair.Value)))
                                .ToLookup(item => item.Key, StringComparer.Ordinal);
        var result = new List<DecidingGap>();
        foreach (var gap in scope.Gaps.OrderByDescending(gap => gap.Roots).ThenByDescending(gap => gap.Regions)
                                      .ThenByDescending(gap => gap.CallSites).ThenBy(gap => gap.Callee, StringComparer.Ordinal))
        {
            WorkCancellation.Check();
            var decided = decisions[gap.Callee].GroupBy(item => item.Fingerprint, StringComparer.Ordinal)
                                          .OrderBy(group => group.Key, StringComparer.Ordinal)
                                          .Select(group => new DecidingGapFinding(group.Key,
                                              new[] { GapComponents.OPERATION, GapComponents.OVERLAP, GapComponents.PROTECTION }
                                                  .Where(check => group.Any(item => item.DecidedChecks.Contains(check, StringComparer.Ordinal))).ToArray()))
                                          .ToArray();
            if (decided.Length == 0)
                continue;
            var sites = gap.Sites.Select(site => sources[site]).Distinct()
                           .OrderBy(site => site.Path, StringComparer.Ordinal).ThenBy(site => site.Line)
                           .Select(site => site.ToString()).ToArray();
            result.Add(new DecidingGap(gap.Callee, decided, sites));
        }
        return result;
    }
}
