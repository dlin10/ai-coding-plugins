using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// Loads a solution with MSBuild's project loader, restoring its packages first when a project needs it, and follows its
/// files on disk from then on. Roslyn's build host finds MSBuild from each project's own directory, so a
/// <c>global.json</c> that pins an older SDK is honoured inside the one daemon process.
/// </summary>
/// <param name="restore">Checks the solution's projects, and restores the solution when one needs it.</param>
internal sealed class MsBuildSolutionLoader(PackageRestore restore) : ISolutionLoader
{
	/// <summary>
	/// They outrank what a project sets, which is the point: a repository that turns its warnings into errors is
	/// entitled to, but a design-time build that fails because of it loses the whole project. The same set
	/// cache-detective loads with.
	/// </summary>
	private static readonly ImmutableDictionary<string, string> ReadOnlyProperties = new Dictionary<string, string>
	{
		["NuGetAudit"] = "false",
		["TreatWarningsAsErrors"] = "false",
		["MSBuildTreatWarningsAsErrors"] = "false",
		["WarningsAsErrors"] = string.Empty
	}.ToImmutableDictionary();

	public async Task<LoadedSolution> LoadAsync(string solutionPath)
	{
		if (!File.Exists(solutionPath))
			throw new FileNotFoundException($"No solution file at {solutionPath}.", solutionPath);

		var workspace = new SolutionWorkspace();
		var sync = new DiskSync(workspace, new MSBuildProjectLoader(workspace, ReadOnlyProperties), restore, solutionPath);
		try
		{
			// An unrestored project loads with no error at all, and then compiles without the types of its packages. A
			// restore that fails leaves the load to go on, and the report says which projects it affects.
			await sync.LoadAsync().ConfigureAwait(false);
			sync.Watch();
			return new LoadedSolution(workspace, DateTimeOffset.UtcNow, restore: null, sync);
		}
		catch
		{
			sync.Dispose();
			workspace.Dispose();
			throw;
		}
	}
}
