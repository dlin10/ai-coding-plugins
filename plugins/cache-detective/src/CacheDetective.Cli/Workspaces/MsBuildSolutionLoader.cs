using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CacheDetective.Workspaces;

public sealed class MsBuildSolutionLoader
{
    private static readonly Lazy<VisualStudioInstance> RegisteredInstance = new(
        MSBuildLocator.RegisterDefaults, LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<MsBuildLoadResult> LoadAsync(string path,
                                                   CancellationToken cancellationToken = default)
    {
        _ = RegisteredInstance.Value;
        var workspace = MSBuildWorkspace.Create();
        var diagnostics = new ConcurrentQueue<WorkspaceDiagnostic>();
#pragma warning disable CS0618 // The adapter contract requires collecting the WorkspaceFailed event.
        workspace.WorkspaceFailed += OnWorkspaceFailed;
#pragma warning restore CS0618

        try
        {
            var fullPath = Path.GetFullPath(path);
            var extension = Path.GetExtension(fullPath);
            var solution = extension.ToLowerInvariant() switch
            {
                ".sln" or ".slnx" => await workspace.OpenSolutionAsync(fullPath,
                    cancellationToken: cancellationToken),
                ".csproj" => (await workspace.OpenProjectAsync(fullPath,
                    cancellationToken: cancellationToken)).Solution,
                _ => throw new ArgumentException(
                    "Expected a .sln, .slnx, or .csproj path.", nameof(path))
            };

            return new MsBuildLoadResult(workspace, TakeFirstTargetPerProject(solution), diagnostics,
                OnWorkspaceFailed);
        }
        catch
        {
#pragma warning disable CS0618
            workspace.WorkspaceFailed -= OnWorkspaceFailed;
#pragma warning restore CS0618
            workspace.Dispose();
            throw;
        }

        void OnWorkspaceFailed(object? sender, WorkspaceDiagnosticEventArgs eventArgs) =>
            diagnostics.Enqueue(eventArgs.Diagnostic);
    }

    private static Solution TakeFirstTargetPerProject(Solution solution)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateTargets = new List<ProjectId>();
        foreach (var project in solution.Projects)
        {
            var identity = project.FilePath is null
                ? project.Id.ToString()
                : Path.GetFullPath(project.FilePath);
            if (!seenPaths.Add(identity))
                duplicateTargets.Add(project.Id);
        }

        foreach (var duplicateTarget in duplicateTargets)
            solution = solution.RemoveProject(duplicateTarget);
        return solution;
    }
}

public sealed class MsBuildLoadResult : IDisposable
{
    private MSBuildWorkspace? _workspace;
    private readonly ConcurrentQueue<WorkspaceDiagnostic> _diagnostics;
    private readonly EventHandler<WorkspaceDiagnosticEventArgs> _workspaceFailedHandler;

    internal MsBuildLoadResult(MSBuildWorkspace workspace, Solution solution,
                               ConcurrentQueue<WorkspaceDiagnostic> diagnostics,
                               EventHandler<WorkspaceDiagnosticEventArgs> workspaceFailedHandler)
    {
        _workspace = workspace;
        Solution = solution;
        _diagnostics = diagnostics;
        _workspaceFailedHandler = workspaceFailedHandler;
    }

    public Solution Solution { get; }

    public IReadOnlyList<WorkspaceDiagnostic> Diagnostics => _diagnostics.ToArray();

    public void Dispose()
    {
        var workspace = Interlocked.Exchange(ref _workspace, null);
        if (workspace is null)
            return;

#pragma warning disable CS0618
        workspace.WorkspaceFailed -= _workspaceFailedHandler;
#pragma warning restore CS0618
        workspace.Dispose();
    }
}
