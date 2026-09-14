using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

public static class FixtureSolution
{
    private const string StubSource = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class NonActionAttribute : System.Attribute { }
        }
        """;

    private static readonly IReadOnlyList<MetadataReference> References =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException(
            "TRUSTED_PLATFORM_ASSEMBLIES is unavailable."))
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    public static Solution Create(params (string Path, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = AddProject(workspace.CurrentSolution, projectId, "Fixture");
        solution = AddDocument(solution, projectId, "AspNetCoreStubs.cs", StubSource,
            @"C:\fixture\AspNetCoreStubs.cs");
        foreach (var file in files)
        {
            solution = AddDocument(solution, projectId, file.Path, file.Source,
                Path.Combine(@"C:\fixture", file.Path));
        }

        Validate(solution);
        return solution;
    }

    public static Solution CreateProjects(params (string Project, string Path, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        foreach (var group in files.GroupBy(file => file.Project, StringComparer.Ordinal))
        {
            var projectId = ProjectId.CreateNewId();
            solution = AddProject(solution, projectId, group.Key);
            solution = AddDocument(solution, projectId, "AspNetCoreStubs.cs", StubSource,
                Path.Combine(@"C:\fixture", group.Key, "AspNetCoreStubs.cs"));
            foreach (var file in group)
            {
                solution = AddDocument(solution, projectId, file.Path, file.Source,
                    Path.Combine(@"C:\fixture", group.Key, file.Path));
            }
        }

        Validate(solution);
        return solution;
    }

    private static Solution AddProject(Solution solution, ProjectId projectId, string name)
    {
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name,
            name,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            metadataReferences: References);
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
