using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RoslynMcpExtension.Services;

namespace RoslynMcpExtension;

[ProvideBindingPath]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Roslyn MCP Extension", "Exposes Roslyn code analysis via MCP", "1.7.0")]
[ProvideOptionPage(typeof(SettingsPage), "Roslyn MCP Extension", "General", 0, 0, true)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[Guid("b8a7f3e2-1c4d-4e5f-9a6b-8c7d0e1f2a3b")]
[ProvideMenuResource("Menus.ctmenu", 1)]
public sealed class RoslynMcpPackage : AsyncPackage
{
    public static RoslynMcpPackage? Instance { get; private set; }

    internal OutputLogger? Logger { get; private set; }
    internal RoslynAnalysisService? AnalysisService { get; private set; }
    internal McpServerController? Controller { get; private set; }

    private IVsSolution? _solutionService;
    private uint _solutionEventsCookie;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        Instance = this;

        try
        {
            Logger = OutputLogger.Create();
            Logger?.Log("Extension loading...");

            if (await GetServiceAsync(typeof(SComponentModel)) is not IComponentModel componentModel)
            {
                Logger?.Log("Failed to obtain IComponentModel service");
                return;
            }

            AnalysisService = componentModel.GetService<RoslynAnalysisService>();
            AnalysisService.Logger = Logger;

            var analysisService = AnalysisService;
            var logger = Logger;
            Controller = new McpServerController(options => McpServerSession.StartAsync(options, analysisService, logger), logger)
            {
                StartFailedAsync = ShowStartupFailureAsync
            };

            await ServerCommands.InitializeAsync(this);

            // Re-resolve the port and restart the server whenever the loaded solution changes.
            _solutionService = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            _solutionService?.AdviseSolutionEvents(new SolutionEventsSink(this), out _solutionEventsCookie);

            var settings = (SettingsPage)GetDialogPage(typeof(SettingsPage));
            if (settings.AutoStart)
            {
                Logger?.Log("Auto-starting MCP server...");
                RequestEnsureServer();
            }

            Logger?.Log("Extension loaded");
        }
        catch (Exception ex)
        {
            Logger?.Log($"Extension initialization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the per-solution configuration and queues a start, or a restart when the port
    /// changed. Reading the configuration here rather than inside the queued work is deliberate:
    /// GetDialogPage and GetSolutionInfo require the UI thread and every caller is already on it,
    /// so no thread hop can reorder the close/open pair a solution reload produces.
    /// </summary>
    internal void RequestEnsureServer()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Controller == null) return;

        try
        {
            var settings = (SettingsPage)GetDialogPage(typeof(SettingsPage));
            var solutionDirectory = GetCurrentSolutionDirectory();
            var port = RoslynMcpConfig.ResolvePort(solutionDirectory, settings.Port, out var configPath);

            Controller.EnqueueEnsure(new McpServerSessionOptions(port, settings.ServerName, solutionDirectory, configPath));
        }
        catch (Exception ex)
        {
            Logger?.Log($"Could not resolve the MCP server configuration: {ex.Message}");
        }
    }

    internal void RequestStopServer() => Controller?.EnqueueStop();

    private async Task ShowStartupFailureAsync(McpServerSessionOptions options, ServerStartResult result)
    {
        var configSource = options.ConfigPath ?? (options.SolutionDirectory == null
            ? RoslynMcpConfig.FileName
            : Path.Combine(options.SolutionDirectory, RoslynMcpConfig.FileName));
        var message = $"Roslyn MCP server failed to start on port {options.Port}. Another Visual Studio instance may already be using that port. Configuration: {configSource}. {result.Message}";

        await JoinableTaskFactory.SwitchToMainThreadAsync();
        var infoBar = await VS.InfoBar.CreateAsync(new InfoBarModel(message, KnownMonikers.StatusWarning, true));
        if (infoBar != null)
            await infoBar.TryShowInfoBarUIAsync();
    }

    private string? GetCurrentSolutionDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_solutionService?.GetSolutionInfo(out var dir, out _, out _) == VSConstants.S_OK && !string.IsNullOrEmpty(dir))
            return dir;
        return null;
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

            // Terminate rather than negotiate: VS is exiting and the UI thread must not wait.
            Controller?.ShutdownFast();
            Instance = null;
        }

        base.Dispose(disposing);
    }

    private sealed class SolutionEventsSink(RoslynMcpPackage package) : IVsSolutionEvents
    {
        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            package.RequestEnsureServer();
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseSolution(object pUnkReserved)
        {
            package.RequestStopServer();
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
