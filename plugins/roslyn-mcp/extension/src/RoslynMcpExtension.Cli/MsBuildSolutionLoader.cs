using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// Loads a solution with MSBuildWorkspace. Roslyn's build host finds MSBuild from each project's own directory, so a
/// <c>global.json</c> that pins an older SDK is honoured inside the one daemon process.
/// </summary>
internal sealed class MsBuildSolutionLoader : ISolutionLoader
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

	public async Task<Workspace> LoadAsync(string solutionPath)
	{
		if (!File.Exists(solutionPath))
			throw new FileNotFoundException($"No solution file at {solutionPath}.", solutionPath);

		var workspace = MSBuildWorkspace.Create(ReadOnlyProperties);
		try
		{
			await workspace.OpenSolutionAsync(solutionPath).ConfigureAwait(false);
			return workspace;
		}
		catch
		{
			workspace.Dispose();
			throw;
		}
	}
}
