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

/// <summary>A solution the daemon holds, and the analysis served from it.</summary>
/// <param name="workspace">The workspace the solution was loaded into.</param>
/// <param name="loadedAt">When the load finished.</param>
/// <param name="restore">What was restored before the load, or null when the loader restores nothing.</param>
/// <param name="sync">Keeps the workspace in step with the disk, and reports the restores; null when nothing does.</param>
internal sealed class LoadedSolution(Workspace workspace, DateTimeOffset loadedAt, RestoreReport? restore, DiskSync? sync = null)
	: IDisposable
{
	public AnalysisService Analysis { get; } = new(workspace);
	public Solution Solution => workspace.CurrentSolution;
	public DateTimeOffset LoadedAt { get; } = loadedAt;
	public RestoreReport? Restore => sync?.Report ?? restore;
	public int DocumentCount => Solution.Projects.Sum(project => project.DocumentIds.Count);

	/// <summary>Raised when the solution file is found gone.</summary>
	public event Action? Vanished
	{
		add { if (sync != null) sync.Vanished += value; }
		remove { if (sync != null) sync.Vanished -= value; }
	}

	/// <summary>Brings the workspace up to date with the disk before a call; false when the solution file is gone.</summary>
	/// <param name="files">The files the call names, checked whether or not a change to them was reported.</param>
	public Task<bool> CatchUpAsync(IEnumerable<string?> files) => sync?.CatchUpAsync(files) ?? Task.FromResult(true);

	public void Dispose()
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
/// </summary>
internal sealed class SolutionRegistry : IDisposable
{
	public static readonly TimeSpan DefaultUnloadAfter = TimeSpan.FromHours(2);
	public static readonly TimeSpan DefaultExitAfter = TimeSpan.FromMinutes(30);

	/// <summary>How late an unload or the exit may come at most.</summary>
	private static readonly TimeSpan LongestSweep = TimeSpan.FromMinutes(1);

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
	/// the solution's time without calls starts again.
	/// </summary>
	/// <param name="solutionPath">The full path of the .sln or .slnx file, compared ignoring letter case.</param>
	public Task<LoadedSolution> Load(string solutionPath)
	{
		Entry entry;
		lock (_solutions)
		{
			if (!_solutions.TryGetValue(solutionPath, out entry!))
				_solutions[solutionPath] = entry = new Entry(solutionPath, this);
			entry.LastCall = _clock.GetUtcNow();
		}

		// Outside the lock: a load runs its first steps on the caller's thread.
		return entry.Load.Value;
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

	/// <summary>Runs one load, forgetting it if it fails, and unloads the solution once its file is gone.</summary>
	/// <param name="entry">The registry's entry for the solution.</param>
	private async Task<LoadedSolution> LoadAsync(Entry entry)
	{
		LoadedSolution loaded;
		try
		{
			loaded = await _loader.LoadAsync(entry.Path).ConfigureAwait(false);
		}
		catch
		{
			Forget(entry);
			throw;
		}

		loaded.Vanished += () => Unload(entry);
		return loaded;
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
			Load = new Lazy<Task<LoadedSolution>>(() => registry.LoadAsync(this));
		}

		public string Path { get; }
		public Lazy<Task<LoadedSolution>> Load { get; }

		/// <summary>Read and written under the registry's lock.</summary>
		public DateTimeOffset LastCall { get; set; }
	}
}
