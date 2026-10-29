using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using RoslynMcpExtension.Shared;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

[CollectionDefinition(Name)]
public sealed class InProcessMSBuildCollection : ICollectionFixture<InProcessMSBuild>
{
	public const string Name = "In-process MSBuild";
}

/// <summary>Registers the newest SDK's MSBuild in this process, once, before any test evaluates a project.</summary>
public sealed class InProcessMSBuild
{
	public InProcessMSBuild()
	{
		if (!MSBuildLocator.IsRegistered)
			MSBuildLocator.RegisterDefaults();
	}
}

/// <summary>
/// The restore check on real projects, evaluated in this process as the daemon evaluates them, and restored by a real
/// <c>dotnet restore</c>. Newtonsoft.Json 13.0.3 comes from the global packages folder, or from the feeds when it is not
/// there yet.
/// </summary>
[Collection(InProcessMSBuildCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PackageRestoreTests : IDisposable
{
	private readonly UnrestoredSolutions _solutions = new();
	private readonly PackageRestore _restore = new();

	public void Dispose() => _solutions.Dispose();

	[Fact]
	public async Task UnderTheArtifactsLayoutTheAssetsFileIsFoundAndARestoredSolutionNeedsNoRestore()
	{
		_solutions.Write("Directory.Build.props", "<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput></PropertyGroup></Project>");
		var lib = _solutions.Write(@"Lib\Lib.csproj", UnrestoredSolutions.SdkProject());
		var solution = _solutions.Solution("Artifacts.slnx", @"Lib\Lib.csproj");

		var first = await _restore.RestoreIfNeededAsync(solution);
		var second = await _restore.RestoreIfNeededAsync(solution);

		AssertProjects([lib], first.Restored);
		Assert.Empty(first.Failed);
		Assert.True(File.Exists(_solutions.PathOf(@"artifacts\obj\Lib\project.assets.json")));
		Assert.False(Directory.Exists(_solutions.PathOf(@"Lib\obj")));
		AssertNothingRestored(second);
	}

	[Fact]
	public async Task AnObjFolderPlacedByTheSolutionDirectoryIsLookedForWhereTheSolutionRestoreWritesIt()
	{
		_solutions.Write("Directory.Build.props", @"<Project><PropertyGroup><BaseIntermediateOutputPath>$(SolutionDir)obj\$(MSBuildProjectName)\</BaseIntermediateOutputPath></PropertyGroup></Project>");
		var lib = _solutions.Write(@"Lib\Lib.csproj", UnrestoredSolutions.SdkProject());
		var solution = _solutions.Solution("BySolution.slnx", @"Lib\Lib.csproj");

		var first = await _restore.RestoreIfNeededAsync(solution);
		var second = await _restore.RestoreIfNeededAsync(solution);

		AssertProjects([lib], first.Restored);
		Assert.True(File.Exists(_solutions.PathOf(@"obj\Lib\project.assets.json")));
		AssertNothingRestored(second);
	}

	[Fact]
	public async Task APackageReferenceAddedWithoutARestoreIsFlaggedAndTheProjectRestored()
	{
		var lib = _solutions.Write(@"Lib\Lib.csproj", UnrestoredSolutions.SdkProject());
		var solution = _solutions.Solution("Added.slnx", @"Lib\Lib.csproj");
		await _restore.RestoreIfNeededAsync(solution);

		_solutions.Write(@"Lib\Lib.csproj", UnrestoredSolutions.SdkProject("""<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />"""));
		var added = await _restore.RestoreIfNeededAsync(solution);

		AssertProjects([lib], added.Restored);
		Assert.Empty(added.Failed);
		using var assets = JsonDocument.Parse(File.ReadAllText(_solutions.PathOf(@"Lib\obj\project.assets.json")));
		Assert.True(assets.RootElement.GetProperty("libraries").TryGetProperty("Newtonsoft.Json/13.0.3", out _));
	}

	[Fact]
	public async Task AProjectNamingNoAssetsFileIsNotApplicableAndTriggersNoRestore()
	{
		// An old-style project that references no packages: its evaluation names no assets file, and a restore writes none.
		var legacy = _solutions.Write(@"Legacy\Legacy.csproj", """
			<Project ToolsVersion="15.0">
			  <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" />
			  <PropertyGroup>
			    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
			    <OutputType>Library</OutputType>
			  </PropertyGroup>
			  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
			</Project>
			""");
		var solution = _solutions.Solution("Legacy.slnx", @"Legacy\Legacy.csproj");

		var report = await _restore.RestoreIfNeededAsync(solution);

		AssertProjects([legacy], report.NotApplicable);
		AssertNothingRestored(report);
	}

	[Fact]
	public async Task OnlyTheProjectsGivenAreChecked()
	{
		var first = _solutions.Write(@"First\First.csproj", UnrestoredSolutions.SdkProject());
		_solutions.Write(@"Second\Second.csproj", UnrestoredSolutions.SdkProject());
		var solution = _solutions.Solution("Two.slnx", @"First\First.csproj", @"Second\Second.csproj");
		await _restore.RestoreIfNeededAsync(solution);
		File.Delete(_solutions.PathOf(@"Second\obj\project.assets.json"));

		var firstOnly = await _restore.RestoreIfNeededAsync(solution, [first]);

		AssertNothingRestored(firstOnly);
		Assert.False(File.Exists(_solutions.PathOf(@"Second\obj\project.assets.json")));
	}

	/// <summary>Nothing needed a restore, so none ran.</summary>
	/// <param name="report">The check's report.</param>
	private static void AssertNothingRestored(RestoreReport report)
	{
		Assert.Empty(report.Restored);
		Assert.Empty(report.Failed);
		Assert.Null(report.Errors);
	}

	/// <summary>The projects a report lists, compared as paths are on Windows.</summary>
	/// <param name="expected">The full paths of the project files expected.</param>
	/// <param name="actual">The paths the report lists.</param>
	private static void AssertProjects(string[] expected, IEnumerable<string> actual)
		=> Assert.Equal(expected.Select(Path.GetFullPath), actual.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
}
