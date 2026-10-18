using System.Diagnostics;
using Common.Roslyn;
using ConcurrencyHunter.Analysis;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Runs;

internal sealed record RunAnalysis(bool LoadFailed, bool AnalysisFailed, int ProjectsExpected, int ProjectsLoaded,
                                   IReadOnlyList<string> MissingProjects, AnalysisResult? Result,
                                   IReadOnlyList<string> Diagnostics, double LoadSeconds, double AnalysisSeconds);

internal delegate Task<RunAnalysis> AnalysisStep(string target, CancellationToken cancellationToken);

/// <summary>The analysis of a loaded solution, as <see cref="PhaseOneAnalyzer"/> runs it: a parameter of the run, so that a test can see what
/// the run asks of the analyzer.</summary>
/// <param name="solution">The loaded solution.</param>
/// <param name="rootDirectory">The directory source paths are reported relative to.</param>
/// <param name="cancellationToken">Cancels the analysis.</param>
/// <param name="generation">What the run does with the generated model layer.</param>
internal delegate Task<AnalysisResult> SolutionAnalyzer(Solution solution, string rootDirectory, CancellationToken cancellationToken,
                                                        ModelGeneration generation);

internal static class SolutionAnalysis
{
    internal static Task<RunAnalysis> RunAsync(string target, CancellationToken cancellationToken) =>
        RunAsync(target, cancellationToken, (path, token) => new MsBuildSolutionLoader().LoadAsync(path, token));

    /// <summary>The MCP run's analysis of a target: it loads the solution and analyses it generating the models of the library members no layer
    /// describes (<see cref="ModelGeneration.Generate"/>).</summary>
    /// <param name="target">The solution or directory the run was started on.</param>
    /// <param name="cancellationToken">Cancels the load and the analysis.</param>
    /// <param name="load">Loads the solution.</param>
    /// <param name="analyze">Analyses the loaded solution; <see cref="PhaseOneAnalyzer"/> when omitted.</param>
    internal static async Task<RunAnalysis> RunAsync(string target, CancellationToken cancellationToken,
                                                     Func<string, CancellationToken, Task<MsBuildLoadResult>> load,
                                                     SolutionAnalyzer? analyze = null)
    {
        analyze ??= PhaseOneAnalyzer.AnalyzeAsync;
        var loadTimer = Stopwatch.StartNew();
        MsBuildLoadResult loaded;
        try
        {
            loaded = await load(target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            loadTimer.Stop();
            var diagnostics = new List<string> { $"{error.GetType().Name}: {error.Message}" };
            if (error is MsBuildLoadException loadError)
            {
                diagnostics.AddRange(loadError.Diagnostics.Select(diagnostic => $"{diagnostic.Kind}: {diagnostic.Message}"));
            }

            return new RunAnalysis(true, false, 0, 0, [], null, diagnostics, loadTimer.Elapsed.TotalSeconds, 0);
        }

        using (loaded)
        {
            loadTimer.Stop();
            var coverage = loaded.Coverage;
            if (coverage.ProjectsLoaded == 0)
            {
                var loadDiagnostics = CurrentDiagnostics().ToList();
                loadDiagnostics.Add("No projects loaded.");
                return new RunAnalysis(true, false, coverage.ProjectsExpected, coverage.ProjectsLoaded, coverage.MissingProjects, null, loadDiagnostics,
                                       loadTimer.Elapsed.TotalSeconds, 0);
            }

            var analysisTimer = Stopwatch.StartNew();
            try
            {
                var rootDirectory = Path.GetDirectoryName(Path.GetFullPath(target))!;
                var result = await analyze(loaded.Solution, rootDirectory, cancellationToken, ModelGeneration.Generate).ConfigureAwait(false);
                analysisTimer.Stop();
                return new RunAnalysis(false, false, coverage.ProjectsExpected, coverage.ProjectsLoaded, coverage.MissingProjects, result, CurrentDiagnostics(),
                                       loadTimer.Elapsed.TotalSeconds, analysisTimer.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                analysisTimer.Stop();
                var loadDiagnostics = CurrentDiagnostics().ToList();
                loadDiagnostics.Add(error.Message);
                return new RunAnalysis(false, true, coverage.ProjectsExpected, coverage.ProjectsLoaded, coverage.MissingProjects, null, loadDiagnostics,
                                       loadTimer.Elapsed.TotalSeconds, analysisTimer.Elapsed.TotalSeconds);
            }

            IReadOnlyList<string> CurrentDiagnostics() => loaded.Diagnostics.Select(diagnostic => $"{diagnostic.Kind}: {diagnostic.Message}").ToArray();
        }
    }
}
