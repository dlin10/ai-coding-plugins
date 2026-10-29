using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// How a loaded solution follows edits to project files, imported files and project folders, loaded and restored for real
/// in this process: Lib, App referencing Lib, and Other referencing neither. No watcher runs; each test reports the
/// changes it makes.
/// </summary>
[Collection(InProcessMSBuildCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProjectReloadTests : IDisposable
{
	private readonly UnrestoredSolutions _solutions = new();
	private readonly SolutionWorkspace _workspace = new();
	private readonly string _libProject;
	private readonly string _api;
	private readonly DiskSync _sync;

	public ProjectReloadTests()
	{
		_libProject = _solutions.Write(@"Lib\Lib.csproj", UnrestoredSolutions.SdkProject());
		_api = _solutions.Write(@"Lib\Api.cs", "namespace Lib { public static class Api { public static int Get() => 1; } }");
		_solutions.Write(@"App\App.csproj", UnrestoredSolutions.SdkProject("""<ProjectReference Include="..\Lib\Lib.csproj" />"""));
		_solutions.Write(@"App\Use.cs", "namespace App { static class Use { static object U() => Lib.Api.Get(); } }");
		_solutions.Write(@"Other\Other.csproj", UnrestoredSolutions.SdkProject());
		_solutions.Write(@"Other\Directory.Build.props", "<Project />");
		_solutions.Write(@"Other\O.cs", "namespace Other { static class O { } }");
		var solution = _solutions.Solution("Three.slnx", @"Lib\Lib.csproj", @"App\App.csproj", @"Other\Other.csproj");
		_sync = new DiskSync(_workspace, new MSBuildProjectLoader(_workspace), new PackageRestore(), solution);
	}

	public void Dispose()
	{
		_sync.Dispose();
		_workspace.Dispose();
		_solutions.Dispose();
	}

	[Fact]
	public async Task APackageReferenceAddedToAProjectRestoresAndReloadsItAndItsDependentsAndLeavesTheOthersUntouched()
	{
		await _sync.LoadAsync();
		var before = _workspace.CurrentSolution;
		var otherCompilation = await Project(before, "Other").GetCompilationAsync();

		_solutions.Write(@"Lib\Lib.csproj", UnrestoredSolutions.SdkProject("""<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />"""));
		_solutions.Write(@"Lib\Api.cs", "namespace Lib { public static class Api { public static Newtonsoft.Json.Formatting Get() => Newtonsoft.Json.Formatting.Indented; } }");
		_sync.Changed(_libProject);
		_sync.Changed(_api);
		await _sync.CatchUpAsync([]);
		var after = _workspace.CurrentSolution;

		using (var assets = JsonDocument.Parse(File.ReadAllText(_solutions.PathOf(@"Lib\obj\project.assets.json"))))
			Assert.True(assets.RootElement.GetProperty("libraries").TryGetProperty("Newtonsoft.Json/13.0.3", out _));
		Assert.Contains(_libProject, _sync.Report.Restored, StringComparer.OrdinalIgnoreCase);
		Assert.Empty(_sync.Report.Failed);
		// Reloaded projects get new IDs; App uses a type of the new package through Lib, which needs the package's assembly.
		Assert.NotEqual(Project(before, "Lib").Id, Project(after, "Lib").Id);
		Assert.NotEqual(Project(before, "App").Id, Project(after, "App").Id);
		Assert.Empty(await Errors(Project(after, "Lib")));
		Assert.Empty(await Errors(Project(after, "App")));
		Assert.Equal(Project(before, "Other").Id, Project(after, "Other").Id);
		Assert.Same(otherCompilation, await Project(after, "Other").GetCompilationAsync());
	}

	[Fact]
	public async Task ANewFileACallNamesJoinsItsProjectAndTheProjectsReferencingItFollow()
	{
		await _sync.LoadAsync();
		var before = _workspace.CurrentSolution;

		var extra = _solutions.Write(@"Lib\Extra.cs", "namespace Lib { public static class Extra { public static int Value => 2; } }");
		_solutions.Write(@"App\Use.cs", "namespace App { static class Use { static object U() => Lib.Extra.Value; } }");
		await _sync.CatchUpAsync([extra, _solutions.PathOf(@"App\Use.cs")]);
		var after = _workspace.CurrentSolution;

		Assert.Single(after.GetDocumentIdsWithFilePath(extra));
		Assert.NotEqual(Project(before, "Lib").Id, Project(after, "Lib").Id);
		// App was not reloaded: its reference now leads to the reloaded Lib.
		Assert.Equal(Project(before, "App").Id, Project(after, "App").Id);
		Assert.Equal([Project(after, "Lib").Id], Project(after, "App").ProjectReferences.Select(reference => reference.ProjectId));
		Assert.Empty(await Errors(Project(after, "App")));
	}

	[Fact]
	public async Task AnEditToAnImportedFileReloadsOnlyTheProjectsImportingIt()
	{
		await _sync.LoadAsync();
		var before = _workspace.CurrentSolution;

		var props = _solutions.Write(@"Other\Directory.Build.props",
		                             "<Project><PropertyGroup><DefineConstants>$(DefineConstants);FROM_PROPS</DefineConstants></PropertyGroup></Project>");
		_sync.Changed(props);
		await _sync.CatchUpAsync([]);
		var after = _workspace.CurrentSolution;

		Assert.Contains("FROM_PROPS", Project(after, "Other").ParseOptions!.PreprocessorSymbolNames);
		Assert.NotEqual(Project(before, "Other").Id, Project(after, "Other").Id);
		Assert.Equal(Project(before, "Lib").Id, Project(after, "Lib").Id);
		Assert.Equal(Project(before, "App").Id, Project(after, "App").Id);
	}

	private static Project Project(Solution solution, string name) => solution.Projects.Single(project => project.Name == name);

	private static async Task<Diagnostic[]> Errors(Project project)
		=> [.. (await project.GetCompilationAsync())!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];
}
