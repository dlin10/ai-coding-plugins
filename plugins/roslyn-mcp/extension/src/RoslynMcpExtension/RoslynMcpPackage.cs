using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.OperationProgress;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;
using Task = System.Threading.Tasks.Task;

namespace RoslynMcpExtension;

[ProvideBindingPath]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Roslyn MCP Extension", "Exposes Roslyn code analysis via MCP", ExtensionVersion.Value)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[Guid("b8a7f3e2-1c4d-4e5f-9a6b-8c7d0e1f2a3b")]
public sealed class RoslynMcpPackage : AsyncPackage
{
    internal OutputLogger? Logger { get; private set; }
    internal RoslynAnalysisService? AnalysisService { get; private set; }
    internal SolutionPipeHost? Pipes { get; private set; }

    private IVsSolution? _solutionService;
    private uint _solutionEventsCookie;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        try
        {
            Logger = OutputLogger.Create();

            if (await GetServiceAsync(typeof(SComponentModel)) is not IComponentModel componentModel)
            {
                Logger?.Log("Failed to obtain IComponentModel service");
                return;
            }

            AnalysisService = componentModel.GetService<RoslynAnalysisService>();
            AnalysisService.Logger = Logger;

            var analysisService = AnalysisService;
            var operationProgress = await GetServiceAsync(typeof(SVsOperationProgress)) as IVsOperationProgressStatusService;
            Pipes = new SolutionPipeHost(path => SolutionPipe.Open(path, analysisService),
                                         token => SolutionLoadedAsync(operationProgress, token),
                                         Logger);

            // Serve every solution Visual Studio opens to the Roslyn MCP daemon, from when it has loaded until it closes.
            _solutionService = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            _solutionService?.AdviseSolutionEvents(new SolutionEventsSink(this), out _solutionEventsCookie);

            // The package loads in the background, often after the solution has opened.
            if (GetOpenSolutionPath() is { } solutionPath)
                Pipes.SolutionOpened(solutionPath);

            Logger?.Log("Extension loaded");
        }
        catch (Exception ex)
        {
            Logger?.Log($"Extension initialization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Completes once Visual Studio has loaded the solution it opened, IntelliSense included, which is when its workspace
    /// holds every project; at once when the status service is missing.
    /// </summary>
    /// <param name="operationProgress">Visual Studio's status of the operations a solution load runs; null when missing.</param>
    /// <param name="cancellationToken">Cancelled when the solution closes first.</param>
    private static async Task SolutionLoadedAsync(IVsOperationProgressStatusService? operationProgress, CancellationToken cancellationToken)
    {
        if (operationProgress == null) return;

        var stage = operationProgress.GetStageStatusForSolutionLoad(CommonOperationProgressStageIds.Intellisense);
        await stage.WaitForCompletionAsync().WithCancellation(cancellationToken);
    }

    /// <summary>The full path of the open solution's file; null when no solution is open, or a folder is.</summary>
    private string? GetOpenSolutionPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_solutionService?.GetSolutionInfo(out _, out var file, out _) != VSConstants.S_OK || string.IsNullOrEmpty(file))
            return null;

        var extension = Path.GetExtension(file);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? file
            : null;
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposing)
        {
            Logger?.Log("Extension shutting down...");

            if (_solutionService != null && _solutionEventsCookie != 0)
            {
                _solutionService.UnadviseSolutionEvents(_solutionEventsCookie);
                _solutionEventsCookie = 0;
            }

            Pipes?.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class SolutionEventsSink(RoslynMcpPackage package) : IVsSolutionEvents
    {
        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package.GetOpenSolutionPath() is { } solutionPath)
                package.Pipes?.SolutionOpened(solutionPath);
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseSolution(object pUnkReserved)
        {
            package.Pipes?.SolutionClosing();
            return VSConstants.S_OK;
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        public int OnAfterCloseSolution(object pUnkReserved) => VSConstants.S_OK;
    }
}
