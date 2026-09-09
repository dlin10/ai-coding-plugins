using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CacheDetective.Tests.Fixtures;

internal static class FixtureSolution
{
    public static async Task<Solution> CreateAsync(params string[] sourceFiles)
    {
        Assert.NotEmpty(sourceFiles);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Fixture",
            "Fixture",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            metadataReferences: GetMetadataReferences());
        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        foreach (var sourceFile in sourceFiles)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", sourceFile);
            var source = await File.ReadAllTextAsync(path);
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId),
                Path.GetFileName(path),
                SourceText.From(source),
                filePath: path);
        }

        Assert.True(workspace.TryApplyChanges(solution), "Could not apply the fixture solution to the workspace.");

        var compilation = await workspace.CurrentSolution.GetProject(projectId)!.GetCompilationAsync();
        Assert.NotNull(compilation);
        var errors = compilation.GetDiagnostics()
                                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                                .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));

        return workspace.CurrentSolution;
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences()
    {
        var trustedPlatformAssemblies = Assert.IsType<string>(
            AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"));

        return trustedPlatformAssemblies.Split(Path.PathSeparator)
                                        .Select(path => MetadataReference.CreateFromFile(path));
    }
}
