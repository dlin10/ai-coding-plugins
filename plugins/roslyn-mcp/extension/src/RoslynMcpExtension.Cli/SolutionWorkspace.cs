using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// The workspace a solution is loaded into, with the services MSBuildWorkspace has. Unlike MSBuildWorkspace, which writes
/// every change applied to it back to disk, it takes changes in memory only: the daemon follows the disk and never writes
/// to it. See docs/adr/0009.
/// </summary>
internal sealed class SolutionWorkspace() : Workspace(MSBuildMefHostServices.DefaultServices, WorkspaceKind.MSBuild)
{
	/// <summary>Replaces whatever the workspace holds with a solution loaded from disk, as MSBuildWorkspace opens one.</summary>
	/// <param name="solution">The solution as the project loader read it.</param>
	public void Open(SolutionInfo solution)
	{
		OnSolutionAdded(solution);
		UpdateReferencesAfterAdd();
	}

	/// <summary>Makes a solution derived from the current one current.</summary>
	/// <param name="solution">The solution, derived from <see cref="Workspace.CurrentSolution"/>.</param>
	/// <param name="projectsReloaded">
	/// Projects were loaded from disk into it, so that a reference to another project's output may have to become a
	/// reference to that project, as after <see cref="Open"/>.
	/// </param>
	public void Update(Solution solution, bool projectsReloaded)
	{
		SetCurrentSolution(solution);
		if (projectsReloaded)
			UpdateReferencesAfterAdd();
	}
}
