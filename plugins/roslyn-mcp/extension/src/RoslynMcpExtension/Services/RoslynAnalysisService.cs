using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServices;

namespace RoslynMcpExtension.Services;

/// <summary>The analysis contract served from Visual Studio's workspace, logging to the extension's Output pane.</summary>
/// <param name="workspace">Visual Studio's workspace, whose current solution every call is answered from.</param>
[Export(typeof(RoslynAnalysisService))]
[PartCreationPolicy(CreationPolicy.Shared)]
[method: ImportingConstructor]
public class RoslynAnalysisService(VisualStudioWorkspace workspace) : AnalysisService(workspace)
{
	internal IExtensionLogger? Logger { get; set; }

	protected override void Log(string line) => Logger?.Log(line);
}
