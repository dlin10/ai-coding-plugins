using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class AnalysisBoundaryTests
{
    [Fact]
    public void Analysis_assembly_references_no_Roslyn_assembly()
    {
        var referencedAssemblies = typeof(Finding).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referencedAssemblies,
            assembly => assembly.Name?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Analysis_project_file_has_no_package_or_project_reference()
    {
        var projectPath = RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "src", "ConcurrencyHunter.Analysis", "ConcurrencyHunter.Analysis.csproj");
        var projectFile = File.ReadAllText(projectPath);

        Assert.DoesNotContain("PackageReference", projectFile);
        Assert.DoesNotContain("ProjectReference", projectFile);
    }
}
