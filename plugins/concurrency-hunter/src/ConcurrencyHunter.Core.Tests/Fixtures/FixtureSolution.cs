using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

public sealed record FixtureOptions
{
    public const int DEFAULT_STUB_VERSION = 10;

    /// <summary>The output kind of every project not named in <see cref="ProjectOutputKinds"/>.</summary>
    public OutputKind OutputKind { get; init; } = OutputKind.DynamicallyLinkedLibrary;

    /// <summary>Major version per stub assembly name; stubs not named here are referenced at version 10.</summary>
    public IReadOnlyDictionary<string, int> StubVersions { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<(string Project, string ReferencedProject)> ProjectReferences { get; init; } = [];

    public IReadOnlyDictionary<string, OutputKind> ProjectOutputKinds { get; init; } = new Dictionary<string, OutputKind>();

    /// <summary>Assemblies with these names and no types, referenced at their <see cref="StubVersions"/> entry or 10.</summary>
    public IReadOnlyList<string> ExtraAssemblyNames { get; init; } = [];

    /// <summary>Extra empty assemblies referenced by one project only, on top of <see cref="ExtraAssemblyNames"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectExtraAssemblyNames { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Assembly name per project; a project not named here is compiled as an assembly of its own name.</summary>
    public IReadOnlyDictionary<string, string> ProjectAssemblyNames { get; init; } = new Dictionary<string, string>();

    /// <summary>Stub assemblies no project references, for cases about a framework that is absent.</summary>
    public IReadOnlyList<string> OmittedStubs { get; init; } = [];

    internal int VersionOf(string assemblyName) => StubVersions.GetValueOrDefault(assemblyName, DEFAULT_STUB_VERSION);
}

public static class FixtureSolution
{
    public static Solution Create(params (string Path, string Source)[] files) =>
        Create(new FixtureOptions(), files);

    public static Solution Create(FixtureOptions options, params (string Path, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = AddProject(workspace.CurrentSolution, projectId, "Fixture", @"C:\fixture\Fixture.csproj", options);
        foreach (var file in files)
        {
            solution = AddDocument(solution, projectId, file.Path, file.Source,
                Path.Combine(@"C:\fixture", file.Path));
        }

        Validate(solution);
        return solution;
    }

    public static Solution CreateProjects(params (string Project, string Path, string Source)[] files) =>
        CreateProjects(new FixtureOptions(), files);

    public static Solution CreateProjects(FixtureOptions options, params (string Project, string Path, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var projectIds = new Dictionary<string, ProjectId>(StringComparer.Ordinal);
        foreach (var group in files.GroupBy(file => file.Project, StringComparer.Ordinal))
        {
            var projectId = ProjectId.CreateNewId();
            projectIds.Add(group.Key, projectId);
            solution = AddProject(solution, projectId, group.Key, Path.Combine(@"C:\fixture", group.Key, group.Key + ".csproj"), options);
            foreach (var file in group)
            {
                solution = AddDocument(solution, projectId, file.Path, file.Source,
                    Path.Combine(@"C:\fixture", group.Key, file.Path));
            }
        }

        foreach (var (project, referenced) in options.ProjectReferences)
        {
            if (!projectIds.TryGetValue(project, out var projectId) || !projectIds.TryGetValue(referenced, out var referencedId))
                throw new ArgumentException($"Project reference {project} -> {referenced} names a project with no files.", nameof(options));
            solution = solution.AddProjectReference(projectId, new ProjectReference(referencedId));
        }

        Validate(solution);
        return solution;
    }

    private static Solution AddProject(Solution solution, ProjectId projectId, string name, string filePath, FixtureOptions options)
    {
        var stubs = StubAssemblies.Names.Except(options.OmittedStubs, StringComparer.Ordinal).Select(stub => StubAssemblies.Get(stub, options.VersionOf(stub)));
        var extraNames = options.ExtraAssemblyNames.Concat(options.ProjectExtraAssemblyNames.GetValueOrDefault(name) ?? []).ToArray();
        var extras = extraNames.Select(extra => StubAssemblies.GetEmpty(extra, options.VersionOf(extra)));
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name,
            options.ProjectAssemblyNames.GetValueOrDefault(name, name),
            LanguageNames.CSharp,
            filePath: filePath,
            compilationOptions: new CSharpCompilationOptions(
                options.ProjectOutputKinds.GetValueOrDefault(name, options.OutputKind),
                nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            metadataReferences: StubAssemblies.PlatformWithout(extraNames).Concat(stubs).Concat(extras));
        return solution.AddProject(projectInfo);
    }

    private static Solution AddDocument(Solution solution, ProjectId projectId, string name, string source,
                                        string path) =>
        solution.AddDocument(DocumentId.CreateNewId(projectId), name, SourceText.From(source), filePath: path);

    private static void Validate(Solution solution)
    {
        var errors = solution.Projects
            .SelectMany(project => project.GetCompilationAsync().GetAwaiter().GetResult()?
                .GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ?? [])
            .ToArray();
        if (errors.Length != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }
}
