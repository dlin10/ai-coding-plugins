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
using Microsoft.VisualStudio.Threading;
using RoslynMcpExtension.Services;

namespace RoslynMcpExtension;

[ProvideBindingPath]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Roslyn MCP Extension", "Exposes Roslyn code analysis via MCP", "1.5.0")]
[ProvideOptionPage(typeof(SettingsPage), "Roslyn MCP Extension", "General", 0, 0, true)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[Guid("b8a7f3e2-1c4d-4e5f-9a6b-8c7d0e1f2a3b")]
[ProvideMenuResource("Menus.ctmenu", 1)]
public sealed class RoslynMcpPackage : AsyncPackage
{
    public static RoslynMcpPackage? Instance { get; private set; }

    internal OutputLogger? Logger { get; private set; }
    internal RoslynAnalysisService? AnalysisService { get; private set; }
    internal RpcServer? RpcServer { get; private set; }
    internal ServerProcessManager? ProcessManager { get; private set; }

    private readonly SemaphoreSlim _serverLock = new(1, 1);
    private IVsSolution? _solutionService;
    private uint _solutionEventsCookie;
    private int _currentPort = -1;

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

            RpcServer = new RpcServer(AnalysisService, Logger);
            ProcessManager = new ServerProcessManager(Logger);

            await ServerCommands.InitializeAsync(this);

            // Re-resolve the port and restart the server whenever the loaded solution changes.
            _solutionService = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            _solutionService?.AdviseSolutionEvents(new SolutionEventsSink(this), out _solutionEventsCookie);

            var settings = (SettingsPage)GetDialogPage(typeof(SettingsPage));
            if (settings.AutoStart)
            {
                Logger?.Log("Auto-starting MCP server...");
                EnsureServerAsync().Forget();
            }

            Logger?.Log("Extension loaded");
        }
        catch (Exception ex)
        {
            Logger?.Log($"Extension initialization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the port for the currently loaded solution and ensures the MCP server is running
    /// on it: no-op if already serving that port, restart if the port changed, start if not
    /// running. Serialized so overlapping solution events cannot start two servers.
    /// </summary>
    internal async Task EnsureServerAsync()
    {
        if (RpcServer == null || ProcessManager == null)
            return;

        // GetSolutionInfo / GetDialogPage require the UI thread; resolve there, then do the
        // process work off the UI thread (StartAsync waits on the child process).
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        var settings = (SettingsPage)GetDialogPage(typeof(SettingsPage));
		var solutionDirectory = GetCurrentSolutionDirectory();
		var port = RoslynMcpConfig.ResolvePort(solutionDirectory, settings.Port, out var configPath);
        var serverName = settings.ServerName;

        await TaskScheduler.Default;

        await _serverLock.WaitAsync();
        try
        {
            if (ProcessManager.IsRunning && _currentPort == port)
                return;

            if (ProcessManager.IsRunning)
            {
                Logger?.Log($"Solution changed; restarting MCP server on port {port}...");
                RpcServer.Stop();
                await ProcessManager.StopAsync();
            }

            var pipeName = RpcServer.Start();
			#pragma warning disable VSTHRD003 // RpcServer owns the readiness task for the child process.
			var startResult = await ProcessManager.StartAsync(pipeName, port, serverName, RpcServer.Ready);
			#pragma warning restore VSTHRD003
			if (startResult.Succeeded)
			{
				_currentPort = port;
			}
			else
			{
				RpcServer.Stop();
				_currentPort = -1;
				if (startResult.FailureKind is ServerStartFailureKind.ProcessExited or ServerStartFailureKind.ReadinessTimeout)
					await ShowStartupFailureAsync(port, configPath, solutionDirectory, startResult.Message);
			}
        }
        catch (Exception ex)
        {
            Logger?.Log($"Failed to start MCP server: {ex.Message}");
        }
        finally
        {
            _serverLock.Release();
        }
    }

	private async Task ShowStartupFailureAsync(int port, string? configPath, string? solutionDirectory, string detail)
	{
		var configSource = configPath ?? (solutionDirectory == null
			? RoslynMcpConfig.FileName
			: Path.Combine(solutionDirectory, RoslynMcpConfig.FileName));
		var message = $"Roslyn MCP server failed to start on port {port}. Another Visual Studio instance may already be using that port. Configuration: {configSource}. {detail}";

		await JoinableTaskFactory.SwitchToMainThreadAsync();
		var infoBar = await VS.InfoBar.CreateAsync(new InfoBarModel(message, KnownMonikers.StatusWarning, true));
		if (infoBar != null)
			await infoBar.TryShowInfoBarUIAsync();
	}

    internal async Task StopServerAsync()
    {
        if (ProcessManager == null)
            return;

        await _serverLock.WaitAsync();
        try
        {
            RpcServer?.Stop();
            await ProcessManager.StopAsync();
            _currentPort = -1;
        }
        catch (Exception ex)
        {
            Logger?.Log($"Failed to stop MCP server: {ex.Message}");
        }
        finally
        {
            _serverLock.Release();
        }
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

            _ = ProcessManager?.StopAsync();
            RpcServer?.Dispose();
            _serverLock.Dispose();
            Instance = null;
        }
        base.Dispose(disposing);
    }

    private sealed class SolutionEventsSink(RoslynMcpPackage package) : IVsSolutionEvents
    {
        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            package.EnsureServerAsync().Forget();
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseSolution(object pUnkReserved)
        {
            package.StopServerAsync().Forget();
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
