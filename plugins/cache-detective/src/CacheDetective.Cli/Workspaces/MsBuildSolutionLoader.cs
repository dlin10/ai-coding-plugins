using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CacheDetective.Workspaces;

public sealed class MsBuildSolutionLoader
{
    private static readonly Lazy<VisualStudioInstance> REGISTERED_INSTANCE = new(
        MSBuildLocator.RegisterDefaults, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The global properties every load runs under. They outrank whatever the project file sets, which is
    /// the point: we are reading the semantic model of somebody else's code, not shipping it. A repository
    /// that turns its own warnings into errors is entitled to; a design-time build that fails because of
    /// it costs us the whole project and every finding in it.
    /// <para><c>RunAnalyzers</c> is deliberately <em>not</em> set: switching it off would take the source
    /// generators with it, and a generated partial class is code the graph has to see.</para>
    /// </summary>
    private static readonly Dictionary<string, string> READ_ONLY_PROPERTIES = new(StringComparer.Ordinal)
    {
        ["NuGetAudit"] = "false",
        ["TreatWarningsAsErrors"] = "false",
        ["MSBuildTreatWarningsAsErrors"] = "false",
        ["WarningsAsErrors"] = string.Empty
    };

    /// <summary>Registers the MSBuild instance this process builds against. Registration is process-wide
    /// and may only happen once, so anything else that creates its own workspace has to come through
    /// here rather than register a second time.</summary>
    internal static void EnsureMsBuildRegistered() => _ = REGISTERED_INSTANCE.Value;

    public async Task<MsBuildLoadResult> LoadAsync(string path,
                                                   CancellationToken cancellationToken = default)
    {
        EnsureMsBuildRegistered();
        var workspace = MSBuildWorkspace.Create(READ_ONLY_PROPERTIES);
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

            solution = TakeFirstTargetPerProject(solution);
            solution = DropUnresolvedAnalyzers(solution, diagnostics);
            return new MsBuildLoadResult(workspace, solution, diagnostics, OnWorkspaceFailed,
                                         new LoadCoverage(DeclaredProjects(fullPath), OpenedProjects(solution, diagnostics),
                                                          EmptyProjects(solution, diagnostics)));
        }
        catch (OperationCanceledException)
        {
            Detach();
            throw;
        }
        catch (Exception error)
        {
            // The diagnostics gathered before the throw are the only account of what went wrong on the
            // way, and disposing the workspace is what would otherwise take them with it.
            var collected = diagnostics.ToArray();
            Detach();
            throw new MsBuildLoadException(error, collected);
        }

        void Detach()
        {
#pragma warning disable CS0618
            workspace.WorkspaceFailed -= OnWorkspaceFailed;
#pragma warning restore CS0618
            workspace.Dispose();
        }

        void OnWorkspaceFailed(object? sender, WorkspaceDiagnosticEventArgs eventArgs) => diagnostics.Enqueue(eventArgs.Diagnostic);
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

    /// <summary>Drops the analyzer references whose file is not on disk, and nothing else. An analyzer we
    /// cannot load is a missing diagnostic, not a missing project — but leaving the reference in place
    /// makes every compilation of that project carry the failure instead.</summary>
    private static Solution DropUnresolvedAnalyzers(Solution solution, ConcurrentQueue<WorkspaceDiagnostic> diagnostics)
    {
        foreach (var projectId in solution.ProjectIds)
        {
            var project = solution.GetProject(projectId)!;
            foreach (var reference in project.AnalyzerReferences.ToArray())
            {
                if (reference.FullPath is not { } fullPath || File.Exists(fullPath))
                    continue;

                solution = solution.RemoveAnalyzerReference(projectId, reference);
                diagnostics.Enqueue(new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning,
                    $"Project '{project.Name}' references an analyzer that is not on disk and it was dropped: {fullPath}"));
            }
        }

        return solution;
    }

    /// <summary>
    /// What a solution and the diagnostics its load produced say about coverage. Exposed so that a test
    /// can pose the case this exists for — a project with no documents that a Failure names — by pairing a
    /// solution with diagnostics other than the ones its own load reported, which is not something
    /// <see cref="LoadAsync"/> can be asked for.
    /// </summary>
    internal static LoadCoverage CoverageFor(string entryPoint, Solution solution,
                                             IEnumerable<WorkspaceDiagnostic> diagnostics)
    {
        var reported = diagnostics.ToArray();
        return new LoadCoverage(DeclaredProjects(Path.GetFullPath(entryPoint)), OpenedProjects(solution, reported),
                                EmptyProjects(solution, reported));
    }

    /// <summary>
    /// The projects the load actually opened. A project the workspace listed but whose design-time build
    /// failed arrives with no documents at all, and it used to count as opened: loadComplete said the scan
    /// was whole while that project contributed nothing to the graph. A project with no documents that
    /// nothing failed on is a different thing — an empty project is legitimately empty — so only the ones
    /// a Failure diagnostic names are treated as unopened.
    /// </summary>
    private static IReadOnlyCollection<string> OpenedProjects(Solution solution,
                                                              IEnumerable<WorkspaceDiagnostic> diagnostics) =>
        solution.Projects.Where(project => project.Documents.Any() || !Failed(project, diagnostics))
                .Select(project => project.FilePath)
                .OfType<string>()
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The projects that opened and hold no documents. They are loaded — nothing failed on them —
    /// but a reader is owed the fact that the graph got nothing from them.</summary>
    private static IReadOnlyCollection<string> EmptyProjects(Solution solution,
                                                             IEnumerable<WorkspaceDiagnostic> diagnostics) =>
        solution.Projects.Where(project => !project.Documents.Any() && !Failed(project, diagnostics))
                .Select(project => project.FilePath)
                .OfType<string>()
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToArray();

    /// <summary>Whether a Failure diagnostic names this project. MSBuild reports the failure against the
    /// project file, so the path in the message is what ties the two together.</summary>
    private static bool Failed(Project project, IEnumerable<WorkspaceDiagnostic> diagnostics) =>
        project.FilePath is { } path &&
        diagnostics.Any(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure &&
                                      diagnostic.Message.Contains(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The projects the entry point declares. A <c>.csproj</c> declares itself and nothing else: the
    /// projects it references are opened as a consequence, and are not owed to anybody. A solution
    /// declares the project entries it lists — solution folders are not projects, and a project with
    /// several target frameworks is named once, because <see cref="TakeFirstTargetPerProject"/> has
    /// already reduced it to one.
    /// </summary>
    private static IReadOnlyCollection<string> DeclaredProjects(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (Path.GetExtension(fullPath).ToLowerInvariant())
        {
            case ".csproj":
                declared.Add(fullPath);
                break;
            case ".slnx":
                foreach (var element in XDocument.Load(fullPath).Descendants("Project"))
                {
                    if (element.Attribute("Path")?.Value is { } relative)
                        Declare(relative);
                }
                break;
            case ".sln":
                foreach (var line in File.ReadLines(fullPath).Where(line => line.StartsWith("Project(", StringComparison.Ordinal)))
                {
                    var fields = line[(line.IndexOf('=') + 1)..].Split(',');
                    if (fields.Length >= 2)
                        Declare(fields[1].Trim().Trim('"'));
                }
                break;
        }

        return declared;

        void Declare(string relative)
        {
            // A solution folder's entry carries a name, not a path to a project file. Asking for the
            // extension separates the two without having to know the folder's type GUID.
            if (!Path.GetExtension(relative).EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                return;

            declared.Add(Path.GetFullPath(Path.Combine(directory, relative.Replace('\\', Path.DirectorySeparatorChar))));
        }
    }

    /// <summary>
    /// The project kinds <see cref="MSBuildWorkspace"/> can open at all. Everything else a solution lists —
    /// <c>.dcproj</c>, <c>.sqlproj</c>, <c>.vcxproj</c>, <c>.shproj</c>, <c>.esproj</c> — is not a project
    /// this loader failed to open; it is one it was never going to. Counting them as expected made
    /// eShopOnContainers report <c>loadComplete: false</c> for a <c>docker-compose.dcproj</c> that holds no
    /// code, which reads to the agent as an incomplete scan.
    /// </summary>
    private static readonly string[] OPENABLE_EXTENSIONS = [".csproj", ".vbproj"];

    internal static bool IsOpenable(string path) =>
        OPENABLE_EXTENSIONS.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Which of the declared projects the load actually opened. Counted by <em>composition</em> and never by
/// number: <see cref="MSBuildWorkspace"/> opens referenced projects too, so a declared project that fell
/// out can be made up for by a referenced one that came in, and two equal counts prove nothing at all.
/// </summary>
public sealed class LoadCoverage
{
    private readonly IReadOnlyCollection<string> _declared;
    private readonly IReadOnlyCollection<string> _opened;

    internal LoadCoverage(IReadOnlyCollection<string> declared, IReadOnlyCollection<string> opened,
                          IReadOnlyCollection<string>? empty = null)
    {
        _declared = declared;
        _opened = opened;
        EmptyProjects = empty is null ? [] : [.. empty];
    }

    /// <summary>The projects that opened and hold no documents. Nothing failed on them, so they are not
    /// missing; they simply gave the graph nothing, and a reader is owed that.</summary>
    public IReadOnlyList<string> EmptyProjects { get; }

    /// <summary>The declared projects this loader can open, which is what "expected" can honestly
    /// mean.</summary>
    private IEnumerable<string> Openable => _declared.Where(MsBuildSolutionLoader.IsOpenable);

    public int ProjectsExpected => Openable.Count();

    public int ProjectsLoaded => _opened.Count;

    public IReadOnlyList<string> MissingProjects =>
        Openable.Where(project => !_opened.Contains(project))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToArray();

    /// <summary>
    /// The declared entries no MSBuild workspace opens, with the extension that says why. They are not
    /// missing — a <c>docker-compose.dcproj</c> holds no code to index — but a reader is owed the fact
    /// that the solution listed them and the graph does not contain them.
    /// </summary>
    public IReadOnlyList<string> SkippedProjects =>
        _declared.Where(project => !MsBuildSolutionLoader.IsOpenable(project))
                 .Select(project => $"{Path.GetFileName(project)} ({Path.GetExtension(project)} is not a project MSBuild opens)")
                 .Order(StringComparer.Ordinal)
                 .ToArray();

    public bool LoadComplete => MissingProjects.Count == 0;
}

/// <summary>A load that failed, carrying what the workspace had already reported when it did.</summary>
public sealed class MsBuildLoadException : Exception
{
    internal MsBuildLoadException(Exception inner, IReadOnlyList<WorkspaceDiagnostic> diagnostics)
        : base(inner.Message, inner) => Diagnostics = diagnostics;

    public IReadOnlyList<WorkspaceDiagnostic> Diagnostics { get; }
}

public sealed class MsBuildLoadResult : IDisposable
{
    private MSBuildWorkspace? _workspace;
    private readonly ConcurrentQueue<WorkspaceDiagnostic> _diagnostics;
    private readonly EventHandler<WorkspaceDiagnosticEventArgs> _workspaceFailedHandler;

    internal MsBuildLoadResult(MSBuildWorkspace workspace, Solution solution,
                               ConcurrentQueue<WorkspaceDiagnostic> diagnostics,
                               EventHandler<WorkspaceDiagnosticEventArgs> workspaceFailedHandler,
                               LoadCoverage coverage)
    {
        _workspace = workspace;
        Solution = solution;
        _diagnostics = diagnostics;
        _workspaceFailedHandler = workspaceFailedHandler;
        Coverage = coverage;
    }

    public Solution Solution { get; }

    public LoadCoverage Coverage { get; }

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
