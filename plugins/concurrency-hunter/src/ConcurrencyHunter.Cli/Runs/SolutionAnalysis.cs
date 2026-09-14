using System.Diagnostics;
using Common.Roslyn;
using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Runs;

internal sealed record RunAnalysis(bool LoadFailed, bool AnalysisFailed, int ProjectsExpected, int ProjectsLoaded,
                                   IReadOnlyList<string> MissingProjects, AnalysisResult? Result,
                                   IReadOnlyList<string> Diagnostics, double LoadSeconds, double AnalysisSeconds);

internal delegate Task<RunAnalysis> AnalysisStep(string target, CancellationToken cancellationToken);

internal static class SolutionAnalysis
{
    internal static Task<RunAnalysis> RunAsync(string target, CancellationToken cancellationToken) =>
        RunAsync(target, cancellationToken, (path, token) => new MsBuildSolutionLoader().LoadAsync(path, token));

    internal static async Task<RunAnalysis> RunAsync(string target, CancellationToken cancellationToken,
                                                     Func<string, CancellationToken, Task<MsBuildLoadResult>> load)
    {
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
                var result = await PhaseOneAnalyzer.AnalyzeAsync(loaded.Solution, rootDirectory, cancellationToken).ConfigureAwait(false);
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
