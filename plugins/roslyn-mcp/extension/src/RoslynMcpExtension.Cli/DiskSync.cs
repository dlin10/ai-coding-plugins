using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli;

/// <summary>
/// Keeps one solution's workspace in step with the disk. A file system watcher queues the paths that changed, and each call
/// first applies the queue, together with the files the call names, which are checked whether or not the watcher has
/// reported them yet. After a watcher overflow or error the next call compares every file of the solution instead. An edit
/// to a project file, or to a file a project imports, runs the restore check on the affected projects, which then reload;
/// an edit to the solution file loads the solution again. See docs/adr/0009.
/// </summary>
internal sealed class DiskSync : IDisposable
{
	/// <summary>Past this many paths waiting, comparing every file costs less than looking at each path.</summary>
	private const int MaxPending = 10_000;

	/// <summary>Build output and installed packages, which change on every build; dot-named folders are skipped too.</summary>
	private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };

	private static readonly EnumerationOptions Entries = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

	private readonly SolutionWorkspace _workspace;
	private readonly MSBuildProjectLoader _loader;
	private readonly PackageRestore _restore;
	private readonly string _solutionPath;

	/// <summary>The repository holding the solution, or the solution's folder outside one: the files followed lie in it.</summary>
	private readonly string _tree;

	private readonly SemaphoreSlim _applying = new(1, 1);
	private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<FileSystemWatcher> _watchers = [];
	private volatile bool _rescan;
	private volatile RestoreReport _report = new();

	// Read and written while _applying is held.
	private readonly Dictionary<string, FileStamp?> _stamps = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string[]> _imports = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Follows a solution's files on disk; nothing is followed until the solution is loaded or opened.</summary>
	/// <param name="workspace">The workspace the solution is loaded into.</param>
	/// <param name="loader">Loads the solution, and its projects again when they change.</param>
	/// <param name="restore">Checks projects, and restores the solution when one needs it.</param>
	/// <param name="solutionPath">The full path of the .sln or .slnx file.</param>
	public DiskSync(SolutionWorkspace workspace, MSBuildProjectLoader loader, PackageRestore restore, string solutionPath)
	{
		_workspace = workspace;
		_loader = loader;
		_restore = restore;
		_solutionPath = solutionPath;
		_tree = RepositoryOrFolder(Path.GetDirectoryName(solutionPath)!);
	}

	/// <summary>Raised when the solution file is found gone; the solution is then unloaded.</summary>
	public event Action? Vanished;

	/// <summary>The restore check's outcome for each project, as of the last check that covered it.</summary>
	public RestoreReport Report => _report;

	/// <summary>Checks and restores every project, then loads the solution into the workspace, replacing what it held.</summary>
	public async Task LoadAsync()
	{
		var projects = PackageRestore.SolutionProjects(_solutionPath).ToList();
		_stamps.Clear();
		Stamp([_solutionPath, .. projects]);
		var imports = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
		var report = await _restore.RestoreIfNeededAsync(_solutionPath, projects, imports).ConfigureAwait(false);
		var solution = await _loader.LoadSolutionInfoAsync(_solutionPath).ConfigureAwait(false);
		_imports.Clear();
		Follow(imports);
		_excluded.Clear();
		_report = report;
		Open(solution);
	}

	/// <summary>Puts a solution into the workspace and records how each of its files looks now.</summary>
	/// <param name="solution">The solution as the project loader read it.</param>
	internal void Open(SolutionInfo solution)
	{
		// Before the workspace has the documents, whose text it reads only when first asked for: a change after this is seen.
		Stamp(solution.Projects.SelectMany(Files));
		// A load stamped the solution file before it read it.
		_stamps.TryAdd(_solutionPath, FileStamp.Of(_solutionPath));
		_workspace.Open(solution);
	}

	/// <summary>Starts following the disk: the repository holding the solution, and any project folder outside it.</summary>
	public void Watch()
	{
		var roots = new List<string> { _tree };
		var folders = _workspace.CurrentSolution.Projects.Select(project => Path.GetDirectoryName(project.FilePath))
		                        .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(folder => folder.Length);
		foreach (var folder in folders)
		{
			if (!roots.Any(root => IsWithin(folder, root)))
				roots.Add(folder);
		}

		foreach (var root in roots)
		{
			var watcher = new FileSystemWatcher(root)
			{
				IncludeSubdirectories = true,
				InternalBufferSize = 64 * 1024,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
			};
			watcher.Changed += (_, e) => Reported(root, e.FullPath);
			watcher.Created += (_, e) => Reported(root, e.FullPath);
			watcher.Deleted += (_, e) => Reported(root, e.FullPath);
			watcher.Renamed += (_, e) =>
			{
				Reported(root, e.OldFullPath);
				Reported(root, e.FullPath);
			};
			watcher.Error += (_, _) => Overflowed();
			watcher.EnableRaisingEvents = true;
			_watchers.Add(watcher);
		}
	}

	/// <summary>Queues a changed path for the next call; unloads the solution at once when its file is gone.</summary>
	/// <param name="path">The full path of the file or folder that changed.</param>
	internal void Changed(string path)
	{
		if (IsWithin(_solutionPath, path) && !File.Exists(_solutionPath))
		{
			RaiseVanished();
			return;
		}

		lock (_pending)
		{
			if (_pending.Count < MaxPending)
			{
				_pending.Add(path);
				return;
			}

			_pending.Clear();
		}

		_rescan = true;
	}

	/// <summary>The watcher lost events, so the next call compares every file.</summary>
	internal void Overflowed()
	{
		_rescan = true;
		// A watched folder deleted with the solution in it ends its watcher with an error rather than reporting it.
		if (!File.Exists(_solutionPath))
			RaiseVanished();
	}

	/// <summary>
	/// Applies what changed on disk since the last call: the queued paths, every file after an overflow, and the files the
	/// call names, which the watcher may not have reported yet. False when the solution file is gone; the solution is then
	/// being unloaded.
	/// </summary>
	/// <param name="files">The files the call names; anything but an absolute path is left to the call to reject.</param>
	public async Task<bool> CatchUpAsync(IEnumerable<string?> files)
	{
		var named = files.OfType<string>().Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).ToList();
		await _applying.WaitAsync().ConfigureAwait(false);
		try
		{
			if (!File.Exists(_solutionPath))
			{
				// Before the call answers, so that the solution is no longer held when it learns why.
				Vanished?.Invoke();
				return false;
			}

			var rescan = _rescan;
			_rescan = false;
			List<string> paths;
			lock (_pending)
			{
				paths = [.. _pending, .. named];
				_pending.Clear();
			}

			if (paths.Count > 0 || rescan)
				await ApplyAsync(paths, rescan).ConfigureAwait(false);
			return true;
		}
		finally
		{
			_applying.Release();
		}
	}

	public void Dispose()
	{
		foreach (var watcher in _watchers)
			watcher.Dispose();
	}

	/// <summary>Compares the paths given, or every file of the solution, with how they looked, and updates the workspace.</summary>
	/// <param name="paths">The paths that changed or that a call names.</param>
	/// <param name="rescan">Compare every file of the solution and look for new ones in every project folder.</param>
	private async Task ApplyAsync(List<string> paths, bool rescan)
	{
		var solution = _workspace.CurrentSolution;
		var pass = new Pass(this, solution);
		if (rescan)
		{
			foreach (var file in pass.KnownFiles)
				pass.Examine(file);
			foreach (var project in solution.Projects.Select(project => Path.GetDirectoryName(project.FilePath)).OfType<string>())
				pass.Examine(project);
		}

		foreach (var path in paths)
			pass.Examine(path);

		if (pass.SolutionEdited)
		{
			await LoadAsync().ConfigureAwait(false);
			return;
		}

		var reload = new HashSet<string>(pass.Grown, StringComparer.OrdinalIgnoreCase);
		if (pass.Edited.Count > 0)
		{
			var affected = WithDependents(solution, pass.Edited);
			await RecheckAsync(affected).ConfigureAwait(false);
			reload.UnionWith(affected);
		}

		var next = pass.Next;
		if (reload.Count > 0)
		{
			next = await ReloadAsync(next, reload).ConfigureAwait(false);
			// A reloaded project may have taken in a file it left out before; one it still leaves out is not reloaded for again.
			_excluded.RemoveWhere(file => reload.Any(project => IsWithin(file, Path.GetDirectoryName(project)!)));
			_excluded.UnionWith(pass.Arrived.Where(file => next.GetDocumentIdsWithFilePath(file).IsEmpty));
		}

		if (next != solution)
			_workspace.Update(next, projectsReloaded: reload.Count > 0);
	}

	/// <summary>Runs the restore check on projects whose file or imports changed, and follows their imports anew.</summary>
	/// <param name="projects">The full paths of the project files.</param>
	private async Task RecheckAsync(HashSet<string> projects)
	{
		var imports = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
		var report = await _restore.RestoreIfNeededAsync(_solutionPath, projects.Where(File.Exists), imports).ConfigureAwait(false);
		foreach (var project in projects)
			_imports.Remove(project);
		Follow(imports);
		_report = Merge(_report, report, projects);
	}

	/// <summary>
	/// Loads projects from disk again in place of the ones a solution holds. A project that fails to load keeps its last
	/// state until its file changes again; one whose file is gone is removed.
	/// </summary>
	/// <param name="solution">The solution holding the projects.</param>
	/// <param name="projectPaths">The full paths of the project files.</param>
	private async Task<Solution> ReloadAsync(Solution solution, HashSet<string> projectPaths)
	{
		var reloading = solution.Projects.Where(project => projectPaths.Contains(project.FilePath!)).ToList();
		// The loader resolves references to the projects in the map without loading them again, and gives every project it
		// loads a new ID; so the map holds every project but the ones reloading, whose dependents are pointed at the new IDs.
		var map = ProjectMap.Create();
		foreach (var project in solution.Projects.Where(project => !projectPaths.Contains(project.FilePath!)))
			map.Add(project);

		var loaded = new Dictionary<ProjectId, ProjectInfo>();
		var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in projectPaths.Where(File.Exists))
		{
			try
			{
				foreach (var project in await _loader.LoadProjectInfoAsync(path, map).ConfigureAwait(false))
					loaded.TryAdd(project.Id, project);
			}
			catch (Exception)
			{
				// Typically a project file saved halfway through an edit; the next save loads it again.
				failed.Add(path);
			}
		}

		var replaced = reloading.Where(project => !failed.Contains(project.FilePath!)).ToList();
		var successors = replaced.ToDictionary(project => project.Id, project => Successor(project, loaded.Values));
		Stamp(loaded.Values.SelectMany(Files));
		// In any order: a reference to a project added later is linked when that project arrives.
		foreach (var project in loaded.Values.Where(project => !solution.ContainsProject(project.Id)))
			solution = solution.AddProject(project);
		foreach (var id in solution.ProjectIds)
			solution = Redirect(solution, id, successors);
		foreach (var project in replaced)
			solution = solution.RemoveProject(project.Id);
		return solution;
	}

	/// <summary>The project loaded in place of one, matched by its output when the project file yields several.</summary>
	/// <param name="project">The project as it was.</param>
	/// <param name="loaded">The projects loaded.</param>
	private static ProjectId? Successor(Project project, IEnumerable<ProjectInfo> loaded)
	{
		var candidates = loaded.Where(info => SamePath(info.FilePath, project.FilePath)).ToList();
		return candidates.Count == 1
			? candidates[0].Id
			: candidates.FirstOrDefault(info => SamePath(info.OutputRefFilePath, project.OutputRefFilePath)
			                                    || SamePath(info.OutputFilePath, project.OutputFilePath))?.Id;
	}

	/// <summary>Points a project's references to reloaded projects at what replaced them, dropping those nothing replaced.</summary>
	/// <param name="solution">The solution holding the project.</param>
	/// <param name="projectId">The project.</param>
	/// <param name="successors">What replaced each reloaded project, or null.</param>
	private static Solution Redirect(Solution solution, ProjectId projectId, Dictionary<ProjectId, ProjectId?> successors)
	{
		var references = solution.GetProject(projectId)!.AllProjectReferences;
		if (!references.Any(reference => successors.ContainsKey(reference.ProjectId)))
			return solution;

		return solution.WithProjectReferences(projectId, references
			.Select(reference => !successors.TryGetValue(reference.ProjectId, out var successor)
				                     ? reference
				                     : successor == null ? null : new ProjectReference(successor, reference.Aliases, reference.EmbedInteropTypes))
			.OfType<ProjectReference>()
			// Two targets of a project that now has one both lead to it.
			.Distinct());
	}

	/// <summary>Projects and every project that depends on them, directly or not: where a change to them can reach.</summary>
	/// <param name="solution">The solution holding the projects.</param>
	/// <param name="projectPaths">The full paths of the project files.</param>
	private static HashSet<string> WithDependents(Solution solution, HashSet<string> projectPaths)
	{
		var graph = solution.GetProjectDependencyGraph();
		var affected = new HashSet<string>(projectPaths, StringComparer.OrdinalIgnoreCase);
		foreach (var project in solution.Projects.Where(project => projectPaths.Contains(project.FilePath!)))
		{
			foreach (var dependent in graph.GetProjectsThatTransitivelyDependOnThisProject(project.Id))
				affected.Add(solution.GetProject(dependent)!.FilePath!);
		}

		return affected;
	}

	/// <summary>
	/// The outcome of a check of some projects laid over the earlier outcomes: each project checked again takes its new one,
	/// except that a project restored earlier and needing nothing now still counts as restored.
	/// </summary>
	/// <param name="earlier">The outcomes so far.</param>
	/// <param name="check">The new check's report.</param>
	/// <param name="projects">The projects the new check covered.</param>
	private static RestoreReport Merge(RestoreReport earlier, RestoreReport check, HashSet<string> projects)
	{
		var failed = earlier.Failed.Where(failure => !projects.Contains(failure.Project)).Concat(check.Failed).ToList();
		var unrestored = new HashSet<string>([.. check.Failed.Select(failure => failure.Project), .. check.NotApplicable], StringComparer.OrdinalIgnoreCase);
		return new RestoreReport
		{
			Restored = [.. earlier.Restored.Where(project => !unrestored.Contains(project)).Union(check.Restored, StringComparer.OrdinalIgnoreCase)],
			Failed = failed,
			NotApplicable = [.. earlier.NotApplicable.Where(project => !projects.Contains(project)), .. check.NotApplicable],
			Errors = failed.Count == 0 ? null : check.Errors ?? earlier.Errors
		};
	}

	/// <summary>
	/// Records the files projects import that lie in the followed tree, by project. The SDK's and the packages' own files
	/// change only with an install or a restore.
	/// </summary>
	/// <param name="imports">The files each project imported, as its evaluation found them.</param>
	private void Follow(Dictionary<string, string[]> imports)
	{
		foreach (var (project, files) in imports)
		{
			_imports[project] = [.. files.Where(file => IsWithin(file, _tree) && !InSkippedFolder(_tree, file))];
			foreach (var file in _imports[project].Where(file => !_stamps.ContainsKey(file)))
				_stamps[file] = FileStamp.Of(file);
		}
	}

	private void Stamp(IEnumerable<string> paths)
	{
		foreach (var path in paths)
			_stamps[path] = FileStamp.Of(path);
	}

	/// <summary>Raises <see cref="Vanished"/> off the caller's thread, which may be the watcher that unloading disposes.</summary>
	private void RaiseVanished() => ThreadPool.QueueUserWorkItem(_ => Vanished?.Invoke());

	/// <summary>A project's file and the files of all its documents.</summary>
	/// <param name="project">The project as the loader read it.</param>
	private static IEnumerable<string> Files(ProjectInfo project)
		=> new[] { project.FilePath }.Concat(project.Documents.Concat(project.AdditionalDocuments).Concat(project.AnalyzerConfigDocuments)
		                                            .Select(document => document.FilePath))
		                             .OfType<string>();

	/// <summary>The root of the repository holding a folder, or the folder itself outside one.</summary>
	/// <param name="folder">The full path of the folder.</param>
	private static string RepositoryOrFolder(string folder)
	{
		for (var current = new DirectoryInfo(folder); current != null; current = current.Parent)
		{
			if (Path.Exists(Path.Combine(current.FullName, ".git")))
				return current.FullName;
		}

		return folder;
	}

	/// <summary>Whether a path is a folder or lies below it.</summary>
	/// <param name="path">The full path.</param>
	/// <param name="folder">The full path of the folder.</param>
	private static bool IsWithin(string path, string folder)
		=> path.Equals(folder, StringComparison.OrdinalIgnoreCase)
		   || path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether a path below a folder lies in build output, installed packages or a dot-named folder.</summary>
	/// <param name="folder">The full path of the folder.</param>
	/// <param name="path">The full path below it.</param>
	private static bool InSkippedFolder(string folder, string path)
		=> Path.GetRelativePath(folder, Path.GetDirectoryName(path) ?? folder).Split(Path.DirectorySeparatorChar)
		       .Any(name => name != "." && IsSkipped(name));

	private static bool IsSkipped(string folderName) => folderName.StartsWith('.') || SkippedFolders.Contains(folderName);

	private static bool SamePath(string? one, string? other) => string.Equals(one, other, StringComparison.OrdinalIgnoreCase);

	/// <summary>The paths of a watcher event, unless they lie where nothing the solution consists of changes.</summary>
	/// <param name="root">The folder the watcher watches.</param>
	/// <param name="path">The full path the event reports.</param>
	private void Reported(string root, string path)
	{
		if (!InSkippedFolder(root, path))
			Changed(path);
	}

	/// <summary>The files below a folder, outside build output, installed packages, dot-named folders and junctions.</summary>
	/// <param name="folder">The full path of the folder.</param>
	private static IEnumerable<string> FilesBelow(string folder)
	{
		var pending = new Stack<string>([folder]);
		while (pending.TryPop(out var current))
		{
			foreach (var file in Enumerate(() => Directory.EnumerateFiles(current, "*", Entries)))
				yield return file;
			foreach (var child in Enumerate(() => Directory.EnumerateDirectories(current, "*", Entries)).Where(child => !IsSkipped(Path.GetFileName(child))))
				pending.Push(child);
		}
	}

	/// <summary>An enumeration of a folder, empty when the folder cannot be read or has gone.</summary>
	/// <param name="enumerate">Starts the enumeration.</param>
	private static List<string> Enumerate(Func<IEnumerable<string>> enumerate)
	{
		try
		{
			return [.. enumerate()];
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	/// <summary>How a file looked when last seen; null when it did not exist.</summary>
	/// <param name="LastWriteUtc">When it was last written.</param>
	/// <param name="Length">Its size in bytes.</param>
	private readonly record struct FileStamp(DateTime LastWriteUtc, long Length)
	{
		public static FileStamp? Of(string path)
		{
			var file = new FileInfo(path);
			return file.Exists ? new FileStamp(file.LastWriteTimeUtc, file.Length) : null;
		}
	}

	/// <summary>One comparison of paths with how they looked, collecting what has to change in the workspace.</summary>
	/// <param name="sync">The sync whose records the comparison reads and updates.</param>
	/// <param name="solution">The workspace's solution when the comparison started.</param>
	private sealed class Pass(DiskSync sync, Solution solution)
	{
		private readonly Solution _solution = solution;
		private readonly HashSet<string> _examined = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _projectFiles = new(solution.Projects.Select(project => project.FilePath).OfType<string>(),
		                                                     StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _importedFiles = new(sync._imports.Values.SelectMany(files => files), StringComparer.OrdinalIgnoreCase);
		private List<string>? _knownFiles;

		/// <summary>The solution with the edited documents read anew and the deleted ones removed.</summary>
		public Solution Next { get; private set; } = solution;

		/// <summary>The solution file changed, so the whole solution loads again.</summary>
		public bool SolutionEdited { get; private set; }

		/// <summary>Projects whose file or one of whose imports changed.</summary>
		public HashSet<string> Edited { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Projects with a new file in their folder, which their evaluation may take in.</summary>
		public HashSet<string> Grown { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>The new files behind <see cref="Grown"/>.</summary>
		public List<string> Arrived { get; } = [];

		/// <summary>Every file the solution consists of: itself, its project files, their imports and their documents.</summary>
		public List<string> KnownFiles => _knownFiles ??=
		[
			.. new[] { sync._solutionPath }.Concat(_projectFiles).Concat(_importedFiles)
			                               .Concat(_solution.Projects.SelectMany(DocumentFiles))
			                               .Distinct(StringComparer.OrdinalIgnoreCase)
		];

		/// <summary>Looks at a path once: a file of the solution, a folder, a new file, or a folder that went away.</summary>
		/// <param name="path">The full path.</param>
		public void Examine(string path)
		{
			if (!_examined.Add(path))
				return;

			if (IsKnown(path))
				Compare(path);
			else if (Directory.Exists(path))
			{
				if (IsSkipped(Path.GetFileName(path)))
					return;
				foreach (var file in FilesBelow(path))
					Examine(file);
			}
			else if (File.Exists(path))
				Arrive(path);
			else
			{
				// A folder deleted or renamed away: the watcher reports the folder, not the files that were in it.
				foreach (var file in KnownFiles.Where(file => IsWithin(file, path)))
					Examine(file);
			}
		}

		private bool IsKnown(string path)
			=> SamePath(path, sync._solutionPath) || _projectFiles.Contains(path) || _importedFiles.Contains(path)
			   || !Next.GetDocumentIdsWithFilePath(path).IsEmpty;

		/// <summary>Compares a file of the solution with how it looked, and notes what its change requires.</summary>
		/// <param name="path">The full path of the file.</param>
		private void Compare(string path)
		{
			var stamp = FileStamp.Of(path);
			if (sync._stamps.TryGetValue(path, out var seen) && seen == stamp)
				return;

			sync._stamps[path] = stamp;
			if (SamePath(path, sync._solutionPath))
			{
				SolutionEdited = true;
				return;
			}

			if (_projectFiles.Contains(path))
				Edited.Add(path);
			foreach (var (project, imports) in sync._imports)
			{
				if (imports.Contains(path, StringComparer.OrdinalIgnoreCase))
					Edited.Add(project);
			}

			foreach (var id in Next.GetDocumentIdsWithFilePath(path))
				Next = stamp == null ? Remove(Next, id) : ReadAnew(Next, id, path);
		}

		/// <summary>Notes a file new to the solution that its project's evaluation may take in.</summary>
		/// <param name="path">The full path of the file.</param>
		private void Arrive(string path)
		{
			if (sync._excluded.Contains(path) || Owner(path) is not { } project)
				return;

			var kinds = _solution.Projects.Where(candidate => SamePath(candidate.FilePath, project))
			                    .SelectMany(candidate => DocumentFiles(candidate).Select(Path.GetExtension)
			                                                                     .Append(candidate.Language == LanguageNames.VisualBasic ? ".vb" : ".cs"));
			if (!kinds.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
				return;

			Grown.Add(project);
			Arrived.Add(path);
		}

		/// <summary>The project file whose folder is the nearest above a path, unless the path lies in its build output.</summary>
		/// <param name="path">The full path of the file.</param>
		private string? Owner(string path)
		{
			var owner = _projectFiles.Where(project => IsWithin(path, Path.GetDirectoryName(project)!))
			                         .MaxBy(project => Path.GetDirectoryName(project)!.Length);
			return owner != null && !InSkippedFolder(Path.GetDirectoryName(owner)!, path) ? owner : null;
		}

		private static IEnumerable<string> DocumentFiles(Project project)
			=> project.Documents.Concat(project.AdditionalDocuments).Concat(project.AnalyzerConfigDocuments)
			          .Select(document => document.FilePath).OfType<string>();

		private static Solution Remove(Solution solution, DocumentId id)
			=> solution.GetDocument(id) != null ? solution.RemoveDocument(id)
			   : solution.GetAdditionalDocument(id) != null ? solution.RemoveAdditionalDocument(id)
			   : solution.RemoveAnalyzerConfigDocument(id);

		/// <summary>Points a document at its file, which the workspace reads again when the document is next asked for.</summary>
		/// <param name="solution">The solution holding the document.</param>
		/// <param name="id">The document, of any kind.</param>
		/// <param name="path">The full path of its file.</param>
		private static Solution ReadAnew(Solution solution, DocumentId id, string path)
		{
			var loader = new FileTextLoader(path, defaultEncoding: null);
			return solution.GetDocument(id) != null ? solution.WithDocumentTextLoader(id, loader, PreservationMode.PreserveValue)
			       : solution.GetAdditionalDocument(id) != null ? solution.WithAdditionalDocumentTextLoader(id, loader, PreservationMode.PreserveValue)
			       : solution.WithAnalyzerConfigDocumentTextLoader(id, loader, PreservationMode.PreserveValue);
		}
	}
}
