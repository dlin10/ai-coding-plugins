using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli;

/// <summary>Opens a solution into a workspace of its own.</summary>
internal interface ISolutionLoader
{
	/// <summary>Loads the solution into a workspace of its own.</summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file.</param>
	Task<LoadedSolution> LoadAsync(string solutionPath);
}

/// <summary>
/// A solution the daemon holds: loaded into a workspace of its own, or forwarded to the Visual Studio that has it open.
/// </summary>
/// <param name="loadedAt">When the load finished, or the daemon connected to Visual Studio.</param>
internal abstract class HeldSolution(DateTimeOffset loadedAt) : IDisposable
{
	/// <summary>Answers the analysis calls about the solution.</summary>
	public abstract IRoslynAnalysisRpc Analysis { get; }

	/// <summary>Who answers them, one of <see cref="SolutionServer"/>.</summary>
	public abstract string ServedBy { get; }

	public DateTimeOffset LoadedAt { get; } = loadedAt;
	public virtual RestoreReport? Restore => null;
	public virtual int? ProjectCount => null;
	public virtual int? DocumentCount => null;

	/// <summary>Raised when the solution can no longer be served from here: its file is gone, or Visual Studio closed it.</summary>
	public abstract event Action? Vanished;

	/// <summary>Brings the solution up to date with the disk before a call; false when the solution file is gone.</summary>
	/// <param name="files">The files the call names, checked whether or not a change to them was reported.</param>
	public virtual Task<bool> CatchUpAsync(IEnumerable<string?> files) => Task.FromResult(true);

	public abstract void Dispose();
}

/// <summary>A solution the daemon loaded itself, and the analysis served from it.</summary>
/// <param name="workspace">The workspace the solution was loaded into.</param>
/// <param name="loadedAt">When the load finished.</param>
/// <param name="restore">What was restored before the load, or null when the loader restores nothing.</param>
/// <param name="sync">Keeps the workspace in step with the disk, and reports the restores; null when nothing does.</param>
internal sealed class LoadedSolution(Workspace workspace, DateTimeOffset loadedAt, RestoreReport? restore, DiskSync? sync = null)
	: HeldSolution(loadedAt)
{
	public override IRoslynAnalysisRpc Analysis { get; } = new AnalysisService(workspace);
	public override string ServedBy => SolutionServer.Daemon;
	public Solution Solution => workspace.CurrentSolution;
	public override RestoreReport? Restore => sync?.Report ?? restore;
	public override int? ProjectCount => Solution.ProjectIds.Count;
	public override int? DocumentCount => Solution.Projects.Sum(project => project.DocumentIds.Count);

	/// <summary>Raised when the solution file is found gone.</summary>
	public override event Action? Vanished
	{
		add { if (sync != null) sync.Vanished += value; }
		remove { if (sync != null) sync.Vanished -= value; }
	}

	public override Task<bool> CatchUpAsync(IEnumerable<string?> files) => sync?.CatchUpAsync(files) ?? Task.FromResult(true);

	public override void Dispose()
	{
		sync?.Dispose();
		workspace.Dispose();
	}
}

/// <summary>
/// The solutions this daemon holds, and for how long. Each path is loaded once, however many sessions ask for it and
/// however many ask while it is still loading; a load that failed is forgotten, so that the next request tries again. A
/// solution no call has asked for in the unload time is unloaded, and once the registry has held no solution for the exit
/// time it is idle, and the daemon exits. See docs/adr/0009.
/// <para>
/// A solution that a Visual Studio of this plugin version has open is forwarded to it rather than loaded, from the first
/// call that finds its pipe until Visual Studio closes it; the next call then loads it in the daemon. See docs/adr/0007.
/// </para>
/// </summary>
internal sealed class SolutionRegistry : IDisposable
{
	public static readonly TimeSpan DefaultUnloadAfter = TimeSpan.FromHours(2);
	public static readonly TimeSpan DefaultExitAfter = TimeSpan.FromMinutes(30);

	/// <summary>How late an unload or the exit may come at most.</summary>
	private static readonly TimeSpan LongestSweep = TimeSpan.FromMinutes(1);

	/// <summary>How long the daemon leaves a Visual Studio pipe alone after it refused a connection.</summary>
	private static readonly TimeSpan RetryVisualStudioAfter = TimeSpan.FromMinutes(1);

	private readonly Dictionary<string, Entry> _solutions = new(StringComparer.OrdinalIgnoreCase);
	private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly ISolutionLoader _loader;
	private readonly TimeProvider _clock;
	private readonly TimeSpan _unloadAfter;
	private readonly TimeSpan _exitAfter;
	private readonly ITimer _sweep;
	private DateTimeOffset _lastRemoval;

	/// <summary>A registry on the system clock that unloads and exits after the default times.</summary>
	/// <param name="loader">Loads a solution the registry does not hold yet.</param>
	public SolutionRegistry(ISolutionLoader loader)
		: this(loader, TimeProvider.System, DefaultUnloadAfter, DefaultExitAfter)
	{
	}

	/// <summary>A registry that unloads and exits after the times given, as a clock tells them.</summary>
	/// <param name="loader">Loads a solution the registry does not hold yet.</param>
	/// <param name="clock">Tells the time, and runs the sweep that unloads and finds the registry idle.</param>
	/// <param name="unloadAfter">How long a solution stays loaded without calls.</param>
	/// <param name="exitAfter">How long the registry holds no solution before it is idle.</param>
	public SolutionRegistry(ISolutionLoader loader, TimeProvider clock, TimeSpan unloadAfter, TimeSpan exitAfter)
	{
		_loader = loader;
		_clock = clock;
		_unloadAfter = unloadAfter;
		_exitAfter = exitAfter;
		_lastRemoval = clock.GetUtcNow();
		var period = TimeSpan.FromTicks(Math.Min(LongestSweep.Ticks, Math.Max(1, Math.Min(unloadAfter.Ticks, exitAfter.Ticks) / 10)));
		_sweep = clock.CreateTimer(_ => Sweep(), null, period, period);
	}

	/// <summary>Completes once the registry has held no solution for the exit time: the daemon then exits.</summary>
	public Task Idle => _idle.Task;

	/// <summary>
	/// The load of a solution: the one under way or finished, or a new one when there is neither. Counts as a call, so that
	/// the solution's time without calls starts again. A solution the daemon loaded itself switches to the Visual Studio
	/// that has opened it since, and one Visual Studio served is loaded anew once Visual Studio has closed it.
	/// </summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file, compared ignoring letter case.</param>
	public Task<HeldSolution> Load(string solutionPath)
	{
		Entry entry;
		lock (_solutions)
		{
			if (!_solutions.TryGetValue(solutionPath, out entry!))
				_solutions[solutionPath] = entry = new Entry(solutionPath, this);
			entry.LastCall = _clock.GetUtcNow();
		}

		// Outside the lock: a load runs its first steps on the caller's thread, and looking for Visual Studio's pipe is a
		// system call.
		switch (entry.Held)
		{
			case VisualStudioSolution { Gone: true }:
				// Visual Studio closed the solution or exited, before the event that drops the entry came.
				Unload(entry);
				return Load(solutionPath);
			case LoadedSolution when ConnectToVisualStudio(entry) is { } served:
				return SwitchTo(entry, served);
			default:
				return entry.Load.Value;
		}
	}

	/// <summary>Drops a solution at once, loaded or loading; false when the registry does not hold it.</summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file, compared ignoring letter case.</param>
	public bool Unload(string solutionPath)
	{
		Entry? entry;
		lock (_solutions)
		{
			if (!_solutions.TryGetValue(solutionPath, out entry))
				return false;
		}

		return Unload(entry);
	}

	public void Dispose()
	{
		_sweep.Dispose();
		List<Entry> entries;
		lock (_solutions)
			entries = [.. _solutions.Values];
		foreach (var entry in entries)
			Unload(entry);
	}

	/// <summary>
	/// Runs one load, or connects to the Visual Studio that has the solution open, forgetting the entry if the load fails,
	/// and unloads the solution once its file is gone or Visual Studio closed it.
	/// </summary>
	/// <param name="entry">The registry's entry for the solution.</param>
	private async Task<HeldSolution> LoadAsync(Entry entry)
	{
		HeldSolution loaded;
		try
		{
			HeldSolution? served = ConnectToVisualStudio(entry);
			loaded = served ?? await _loader.LoadAsync(entry.Path).ConfigureAwait(false);
		}
		catch
		{
			Forget(entry);
			throw;
		}

		loaded.Vanished += () => Unload(entry);
		return loaded;
	}

	/// <summary>
	/// A connection to the Visual Studio that has the solution open; null when none has, or when one refused the daemon
	/// less than a minute ago, as a Visual Studio of another account with the same solution open does.
	/// </summary>
	/// <param name="entry">The registry's entry for the solution.</param>
	private VisualStudioSolution? ConnectToVisualStudio(Entry entry)
	{
		var now = _clock.GetUtcNow();
		lock (_solutions)
		{
			if (now < entry.RetryVisualStudioAt)
				return null;
		}

		if (!VisualStudioSolution.IsOpen(entry.Path))
			return null;
		if (VisualStudioSolution.TryConnect(entry.Path, now) is { } served)
			return served;

		lock (_solutions)
			entry.RetryVisualStudioAt = now + RetryVisualStudioAfter;
		return null;
	}

	/// <summary>
	/// Hands a solution the daemon loaded itself to the Visual Studio that has opened it, and releases the daemon's load at
	/// once.
	/// </summary>
	/// <param name="entry">The registry's entry for the daemon's load.</param>
	/// <param name="served">The connection to Visual Studio.</param>
	private Task<HeldSolution> SwitchTo(Entry entry, VisualStudioSolution served)
	{
		var replacement = new Entry(entry.Path, served);
		bool switched;
		lock (_solutions)
		{
			switched = _solutions.TryGetValue(entry.Path, out var held) && held == entry;
			if (switched)
			{
				replacement.LastCall = entry.LastCall;
				_solutions[entry.Path] = replacement;
			}
		}

		if (!switched)
		{
			// Another call switched the solution or unloaded it meanwhile.
			served.Dispose();
			return Load(entry.Path);
		}

		served.Vanished += () => Unload(replacement);
		Release(entry);
		return replacement.Load.Value;
	}

	/// <summary>Drops an entry and releases its solution; false when the entry was already dropped.</summary>
	/// <param name="entry">The entry.</param>
	private bool Unload(Entry entry)
	{
		if (!Forget(entry))
			return false;
		Release(entry);
		return true;
	}

	/// <summary>Releases a dropped entry's solution: at once when it has loaded, else when its load succeeds.</summary>
	/// <param name="entry">The entry, no longer in the registry.</param>
	private static void Release(Entry entry)
	{
		if (entry.Load.IsValueCreated)
		{
			_ = entry.Load.Value.ContinueWith(load => load.Result.Dispose(), CancellationToken.None,
			                                  TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
			                                  TaskScheduler.Default);
		}
	}

	/// <summary>Removes an entry, unless another has taken its place; false when it is no longer there.</summary>
	/// <param name="entry">The entry.</param>
	private bool Forget(Entry entry)
	{
		lock (_solutions)
		{
			if (!_solutions.TryGetValue(entry.Path, out var held) || held != entry)
				return false;
			_solutions.Remove(entry.Path);
			_lastRemoval = _clock.GetUtcNow();
			return true;
		}
	}

	/// <summary>Unloads the solutions without calls for the unload time, and finds the registry idle once it holds none.</summary>
	private void Sweep()
	{
		List<Entry> unused;
		lock (_solutions)
		{
			// Under the lock, so that a call cannot ask for a solution between finding it unused and dropping it.
			var now = _clock.GetUtcNow();
			unused = [.. _solutions.Values.Where(entry => now - entry.LastCall >= _unloadAfter)];
			foreach (var entry in unused)
			{
				_solutions.Remove(entry.Path);
				_lastRemoval = now;
			}

			if (_solutions.Count == 0 && now - _lastRemoval >= _exitAfter)
				_idle.TrySetResult();
		}

		foreach (var entry in unused)
			Release(entry);
	}

	/// <summary>A solution the registry holds: its load, and when a call last asked for it.</summary>
	private sealed class Entry
	{
		/// <summary>An entry whose load starts when first asked for.</summary>
		/// <param name="path">The full path of the .sln or .slnx file.</param>
		/// <param name="registry">The registry that loads it.</param>
		public Entry(string path, SolutionRegistry registry)
		{
			Path = path;
			Load = new Lazy<Task<HeldSolution>>(() => registry.LoadAsync(this));
		}

		/// <summary>An entry for a solution that is already served.</summary>
		/// <param name="path">The full path of the .sln or .slnx file.</param>
		/// <param name="held">The solution.</param>
		public Entry(string path, HeldSolution held)
		{
			Path = path;
			Load = new Lazy<Task<HeldSolution>>(Task.FromResult(held));
		}

		public string Path { get; }
		public Lazy<Task<HeldSolution>> Load { get; }

		/// <summary>The solution once its load has succeeded; null before, and when it failed.</summary>
		public HeldSolution? Held => Load is { IsValueCreated: true, Value: { IsCompletedSuccessfully: true } load } ? load.Result : null;

		/// <summary>Read and written under the registry's lock.</summary>
		public DateTimeOffset LastCall { get; set; }

		/// <summary>When the daemon may try Visual Studio's pipe again after a refusal. Read and written under the registry's lock.</summary>
		public DateTimeOffset RetryVisualStudioAt { get; set; }
	}
}
