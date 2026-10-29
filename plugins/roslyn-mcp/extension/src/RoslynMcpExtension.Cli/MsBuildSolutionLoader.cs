using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// Loads a solution with MSBuildWorkspace, restoring its packages first when a project needs it. Roslyn's build host
/// finds MSBuild from each project's own directory, so a <c>global.json</c> that pins an older SDK is honoured inside the
/// one daemon process.
/// </summary>
/// <param name="restore">Checks the solution's projects, and restores the solution when one needs it.</param>
internal sealed class MsBuildSolutionLoader(PackageRestore restore) : ISolutionLoader
{
	/// <summary>
	/// They outrank what a project sets, which is the point: a repository that turns its warnings into errors is
	/// entitled to, but a design-time build that fails because of it loses the whole project. The same set
	/// cache-detective loads with.
	/// </summary>
	private static readonly Dictionary<string, string> ReadOnlyProperties = new()
	{
		["NuGetAudit"] = "false",
		["TreatWarningsAsErrors"] = "false",
		["MSBuildTreatWarningsAsErrors"] = "false",
		["WarningsAsErrors"] = string.Empty
	};

	public async Task<LoadedSolution> LoadAsync(string solutionPath)
	{
		if (!File.Exists(solutionPath))
			throw new FileNotFoundException($"No solution file at {solutionPath}.", solutionPath);

		// An unrestored project loads with no error at all, and then compiles without the types of its packages. A restore
		// that fails leaves the load to go on, and the report says which projects it affects.
		var restored = await restore.RestoreIfNeededAsync(solutionPath).ConfigureAwait(false);
		var workspace = MSBuildWorkspace.Create(ReadOnlyProperties);
		try
		{
			await workspace.OpenSolutionAsync(solutionPath).ConfigureAwait(false);
			return new LoadedSolution(workspace, DateTimeOffset.UtcNow, restored);
		}
		catch
		{
			workspace.Dispose();
			throw;
		}
	}
}
