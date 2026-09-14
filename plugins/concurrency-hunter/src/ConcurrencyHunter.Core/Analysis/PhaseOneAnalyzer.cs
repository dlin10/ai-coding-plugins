using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

public static class PhaseOneAnalyzer
{
    public static async Task<AnalysisResult> AnalyzeAsync(Solution solution, string rootDirectory, CancellationToken cancellationToken)
    {
        var inventory = await CollectAsync(solution, rootDirectory, cancellationToken).ConfigureAwait(false);
        var conflicts = ConflictFindings.Create(inventory.Accesses, cancellationToken);
        return new AnalysisResult(inventory.Roots, inventory.Accesses, conflicts.Findings, conflicts.Groups);
    }

    public static async Task<AccessInventory> CollectAsync(Solution solution, string rootDirectory, CancellationToken cancellationToken)
    {
        var roots = new List<ExecutionRoot>();
        var accesses = new List<StaticAccess>();
        foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                continue;

            foreach (var discoveredRoot in ControllerRoots.Discover(compilation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                roots.Add(discoveredRoot.Root);
                accesses.AddRange(StaticAccesses.Discover(compilation, discoveredRoot, rootDirectory, cancellationToken));
            }
        }

        return new AccessInventory(roots.OrderBy(root => root.Symbol, StringComparer.Ordinal).ToArray(),
                                   accesses.OrderBy(access => access.Root.Symbol, StringComparer.Ordinal)
                                           .ThenBy(access => access.Source.Path, StringComparer.Ordinal)
                                           .ThenBy(access => access.Source.StartLine)
                                           .ThenBy(access => access.Source.StartColumn)
                                           .ToArray());
    }
}
