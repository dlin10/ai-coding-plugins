using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// Restores a solution's packages when one of its projects needs it, decided the way the Roslyn language server decides:
/// a project needs a restore when the assets file its evaluation names is missing or unreadable, or does not resolve a
/// package it references. Evaluates the projects in this process, so MSBuildLocator must have registered MSBuild before
/// the first call. See docs/adr/0006.
/// </summary>
internal sealed class PackageRestore
{
	/// <summary>The projects MSBuildWorkspace loads; it leaves any other kind in a solution out, and so does the check.</summary>
	private static readonly string[] CheckedExtensions = [".csproj", ".vbproj"];

	/// <summary>Two restores that share a project would race on its obj folder.</summary>
	private readonly SemaphoreSlim _restoring = new(1, 1);

	/// <summary>Checks every C# and Visual Basic project of a solution, and restores the solution when one needs it.</summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file.</param>
	public Task<RestoreReport> RestoreIfNeededAsync(string solutionPath)
		=> RestoreIfNeededAsync(solutionPath, SolutionProjects(solutionPath));

	/// <summary>Checks some projects of a solution, and restores the solution when one of them needs it.</summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file, which is what a restore restores.</param>
	/// <param name="projectPaths">The full paths of the project files to check.</param>
	/// <param name="imports">
	/// Receives, by project file, the full paths of the files each evaluated project imported, so that an edit to one of
	/// them can be traced to the projects it affects; null when not wanted.
	/// </param>
	public async Task<RestoreReport> RestoreIfNeededAsync(string solutionPath, IEnumerable<string> projectPaths,
	                                                      IDictionary<string, string[]>? imports = null)
	{
		var report = new RestoreReport();
		var needing = Evaluate(solutionPath, projectPaths, report, imports).Where(project => WhyRestore(project) != null).ToList();
		if (needing.Count == 0)
			return report;

		report.Errors = await RestoreAsync(solutionPath).ConfigureAwait(false);
		foreach (var project in needing)
		{
			if (WhyRestore(project) is { } reason)
				report.Failed.Add(new RestoreFailure { Project = project.Path, Reason = reason });
			else
				report.Restored.Add(project.Path);
		}

		return report;
	}

	/// <summary>The full paths of a solution's C# and Visual Basic project files.</summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file.</param>
	public static IEnumerable<string> SolutionProjects(string solutionPath)
		=> SolutionFile.Parse(solutionPath).ProjectsInOrder
		               .Select(project => project.AbsolutePath)
		               .Where(path => CheckedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));

	/// <summary>
	/// The assets file and the package references of each project that names an assets file. A project that names none is
	/// not applicable, and one that cannot be evaluated has failed; both go into the report and need no restore.
	/// </summary>
	/// <param name="solutionPath">The solution the projects are evaluated as part of.</param>
	/// <param name="projectPaths">The project files to evaluate.</param>
	/// <param name="report">Receives the projects that are not applicable or cannot be evaluated.</param>
	/// <param name="imports">Receives the files each evaluated project imported, by project file; null when not wanted.</param>
	private static List<EvaluatedProject> Evaluate(string solutionPath, IEnumerable<string> projectPaths, RestoreReport report,
	                                               IDictionary<string, string[]>? imports)
	{
		// What a solution build passes every project, so that an obj folder placed relative to the solution is looked for
		// where a restore of that solution writes it.
		using var collection = new ProjectCollection(new Dictionary<string, string>
		{
			["SolutionDir"] = Path.GetDirectoryName(solutionPath) + Path.DirectorySeparatorChar,
			["SolutionPath"] = solutionPath,
			["SolutionFileName"] = Path.GetFileName(solutionPath),
			["SolutionName"] = Path.GetFileNameWithoutExtension(solutionPath),
			["SolutionExt"] = Path.GetExtension(solutionPath)
		});
		// How msbuild -restore evaluates for its restore: an unrestored project may import a file that only a restore writes.
		var options = new ProjectOptions
		{
			ProjectCollection = collection,
			LoadSettings = ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreEmptyImports | ProjectLoadSettings.IgnoreInvalidImports
		};

		var evaluated = new List<EvaluatedProject>();
		foreach (var path in projectPaths)
		{
			Project project;
			try
			{
				project = Project.FromFile(path, options);
			}
			catch (InvalidProjectFileException ex)
			{
				report.Failed.Add(new RestoreFailure { Project = path, Reason = $"The project could not be evaluated: {ex.Message}" });
				continue;
			}

			if (imports != null)
				imports[path] = [.. project.Imports.Select(import => import.ImportedProject.FullPath)];
			var assetsFile = project.GetPropertyValue("ProjectAssetsFile");
			if (assetsFile.Length == 0)
				report.NotApplicable.Add(path);
			else
				evaluated.Add(new EvaluatedProject(path, Path.GetFullPath(assetsFile, project.DirectoryPath),
				                                   [.. project.GetItems("PackageReference").Select(item => item.EvaluatedInclude)]));
			collection.UnloadProject(project);
		}

		return evaluated;
	}

	/// <summary>Why a project needs a restore, or null when its assets file resolves every package it references.</summary>
	/// <param name="project">The project, as its evaluation found it.</param>
	private static string? WhyRestore(EvaluatedProject project)
	{
		if (!File.Exists(project.AssetsFile))
			return $"{project.AssetsFile} does not exist.";

		HashSet<string> resolved;
		try
		{
			resolved = ResolvedPackages(project.AssetsFile);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			return $"{project.AssetsFile} could not be read: {ex.Message}";
		}

		var unresolved = project.PackageReferences.Where(name => !resolved.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		return unresolved.Count == 0 ? null : $"{project.AssetsFile} does not resolve {string.Join(", ", unresolved)}.";
	}

	/// <summary>
	/// The names in an assets file's <c>libraries</c>, which holds what the restore resolved, transitive packages included.
	/// Not the dependencies under <c>project.frameworks</c>: those are what was asked for, and a failed restore writes them
	/// too.
	/// </summary>
	/// <param name="assetsFile">The full path of the project.assets.json file.</param>
	private static HashSet<string> ResolvedPackages(string assetsFile)
	{
		using var stream = File.OpenRead(assetsFile);
		using var document = JsonDocument.Parse(stream);
		var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (document.RootElement.TryGetProperty("libraries", out var libraries))
		{
			// Packages and projects alike are keyed "Name/Version".
			foreach (var library in libraries.EnumerateObject())
				resolved.Add(library.Name.Split('/')[0]);
		}

		return resolved;
	}

	/// <summary>Runs dotnet restore on the solution and returns the errors it printed, or null when it succeeded.</summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file.</param>
	private async Task<string?> RestoreAsync(string solutionPath)
	{
		var start = new ProcessStartInfo("dotnet")
		{
			// From the solution's folder, so that a global.json there picks the SDK.
			WorkingDirectory = Path.GetDirectoryName(solutionPath)!,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		// Without the two switches the restore's MSBuild node can hold its output open for minutes after the restore is done.
		foreach (var argument in new[] { "restore", solutionPath, "--disable-build-servers", "-nodeReuse:false", "-p:NuGetAudit=false" })
			start.ArgumentList.Add(argument);
		// MSBuildLocator put this process's MSBuild in the environment, where it would override the SDK a global.json picks.
		foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
			start.Environment.Remove(name);

		await _restoring.WaitAsync().ConfigureAwait(false);
		try
		{
			using var process = Process.Start(start)!;
			var error = process.StandardError.ReadToEndAsync();
			var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false) + await error.ConfigureAwait(false);
			await process.WaitForExitAsync().ConfigureAwait(false);
			if (process.ExitCode == 0)
				return null;

			var errors = output.Split('\n').Select(line => line.Trim()).Where(line => line.Contains(": error ", StringComparison.Ordinal)).Distinct().ToList();
			return errors.Count > 0 ? string.Join("\n", errors) : $"dotnet restore exited with code {process.ExitCode}.";
		}
		finally
		{
			_restoring.Release();
		}
	}

	/// <summary>A project as its evaluation names its assets file and the packages it references.</summary>
	/// <param name="Path">The full path of the project file.</param>
	/// <param name="AssetsFile">The full path of the project.assets.json file its restore writes.</param>
	/// <param name="PackageReferences">The names of its PackageReference items.</param>
	private sealed record EvaluatedProject(string Path, string AssetsFile, IReadOnlyList<string> PackageReferences);
}
