using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

/// <summary>
/// ISSUE70: temporary experimental probe hosted inside VisualStudioWorkspace. Remove in task 4.
/// </summary>
internal sealed class Issue70ProbeService(VisualStudioWorkspace workspace)
{
	private const string ProbeVersion = "1.8.0-issue70";
	private const string SettingsKey = "TextEditor.Roslyn.Specific.SourceGeneratorExecution";
	private BuildEvents? _buildEvents;
	private _dispBuildEvents_OnBuildBeginEventHandler? _vsBuildEvents_OnBuildBegin;
	private _dispBuildEvents_OnBuildDoneEventHandler? _vsBuildEvents_OnBuildDone;

	public Task<Issue70ProbeResult> RunAsync(string operation, string? expectedMemberFullName = null)
	{
		return operation?.Trim().ToLowerInvariant() switch
		{
			"identity" => IdentityAsync(),
			"generator_state" => GeneratorStateAsync(applyBalanced: false),
			"ensure_balanced" => GeneratorStateAsync(applyBalanced: true),
			"build" => BuildAsync(),
			"generated_documents" => GeneratedDocumentsAsync(expectedMemberFullName),
			"workspace_text" => WorkspaceTextAsync(),
			"compare" => CompareAsync(),
			_ => Task.FromResult(Fail($"Unknown issue70 probe operation '{operation}'.", ToolErrorCodes.InvalidArgument))
		};
	}

	private async Task<Issue70ProbeResult> IdentityAsync()
	{
		var result = CreateBase("identity");
		try
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
			FillHostIdentity(result);
			FillWorkspaceCounts(result);
			result.RequestSucceeded = true;
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}

	private async Task<Issue70ProbeResult> CompareAsync()
	{
		var result = await GeneratorStateAsync(false);
		result.Operation = "compare";
		if (!result.RequestSucceeded || !result.BalancedMode) return result;
		try
		{
			var project = workspace.CurrentSolution.Projects.Single(p => p.AssemblyName == "Issue70.Consumer");
			var sources = new List<object>();
			var ordinaryTrees = new HashSet<SyntaxTree>();
			foreach (var document in project.Documents)
			{
				var tree = await document.GetSyntaxTreeAsync();
				if (tree != null) ordinaryTrees.Add(tree);
			}
			foreach (var document in project.Documents.Cast<Microsoft.CodeAnalysis.TextDocument>().Concat(project.AdditionalDocuments))
			{
				var content = (await document.GetTextAsync()).ToString();
				var disk = File.ReadAllText(document.FilePath!);
				if (content != disk) throw new InvalidOperationException("Workspace/disk mismatch: " + document.FilePath);
				sources.Add(new { documentId = document.Id.ToString(), path = document.FilePath,
					version = (await document.GetTextVersionAsync()).ToString(), content,
					sha256 = Sha256(content), diskSha256 = Sha256(disk) });
			}
			object Snapshot(Compilation compilation) => new
			{
				errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
					.Select(d => new { id = d.Id, message = d.GetMessage(), path = d.Location.SourceTree?.FilePath }).ToArray(),
				generated = compilation.SyntaxTrees.Where(t => !ordinaryTrees.Contains(t))
					.Select(t => new { path = t.FilePath, content = t.GetText().ToString(), sha256 = Sha256(t.GetText().ToString()) }).ToArray(),
				memberA = compilation.GetTypeByMetadataName("Issue70Fixture.GeneratedApi")?.GetMembers("MemberA").Length > 0,
				memberB = compilation.GetTypeByMetadataName("Issue70Fixture.GeneratedApi")?.GetMembers("MemberB").Length > 0
			};
			var baselineStarted = DateTimeOffset.UtcNow;
			var watch = Stopwatch.StartNew();
			var baseline = await project.GetCompilationAsync() ?? throw new InvalidOperationException("No compilation");
			var baselineMs = watch.Elapsed.TotalMilliseconds;
			var before = Snapshot(baseline);
			var candidateStarted = DateTimeOffset.UtcNow;
			using var cancellation = new CancellationTokenSource();
			async Task<Compilation> RefreshAsync()
			{
				await project.GetSourceGeneratedDocumentsAsync(cancellation.Token);
				return await project.GetCompilationAsync(cancellation.Token) ?? throw new InvalidOperationException("No candidate");
			}
			watch.Restart();
			var pending = RefreshAsync();
			var timely = await Task.WhenAny(pending, Task.Delay(10000)) == pending;
			var candidateMs = watch.Elapsed.TotalMilliseconds;
			object? after = null;
			string? failure = null;
			if (timely)
			{
				try { after = Snapshot(await pending); }
				catch (Exception ex) { failure = ex.Message; }
			}
			else
			{
				cancellation.Cancel();
				if (await Task.WhenAny(pending, Task.Delay(30000)) != pending)
					throw new InvalidOperationException("Candidate did not quiesce; restart owned host before another trial.");
				try { await pending; } catch (Exception ex) { failure = ex.Message; }
			}
			result.Evidence["comparison"] = JsonConvert.SerializeObject(new
			{
				projectId = project.Id.ToString(), projectVersion = project.Version.ToString(),
				solutionVersion = project.Solution.Version.ToString(), currentSolutionVersion = workspace.CurrentSolution.Version.ToString(),
				sources, baselineStarted, baselineMs, before, candidateStarted, candidateMs,
				timedOut = !timely || candidateMs > 10000, failure, after
			});
			result.RequestSucceeded = true;
		}
		catch (Exception ex) { ToolResultErrors.Set(result, ex); }
		return result;
	}

	private async Task<Issue70ProbeResult> GeneratorStateAsync(bool applyBalanced)
	{
		var result = CreateBase(applyBalanced ? "ensure_balanced" : "generator_state");
		try
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
			FillHostIdentity(result);
			FillWorkspaceCounts(result);

			var workspacesAsm = typeof(Workspace).Assembly;
			result.WorkspacesAssemblyIdentity = workspacesAsm.FullName;
			var iface = workspacesAsm.GetType("Microsoft.CodeAnalysis.Host.IWorkspaceConfigurationService", throwOnError: false);
			if (iface == null)
			{
				result.ErrorCode = ToolErrorCodes.InternalError;
				result.ErrorMessage = "IWorkspaceConfigurationService type not found on loaded Workspaces assembly.";
				result.ManualBalancedAction = ManualBalancedMessage();
				return result;
			}

			result.WorkspaceConfigurationServiceType = iface.AssemblyQualifiedName;
			var getService = typeof(HostWorkspaceServices).GetMethods()
				.FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
			if (getService == null)
			{
				result.ErrorCode = ToolErrorCodes.InternalError;
				result.ErrorMessage = "HostWorkspaceServices.GetService<T>() not found.";
				return result;
			}

			var service = getService.MakeGenericMethod(iface).Invoke(workspace.Services, null);
			if (service == null)
			{
				result.ErrorCode = ToolErrorCodes.InternalError;
				result.ErrorMessage = "IWorkspaceConfigurationService was not available from Workspace.Services.";
				result.ManualBalancedAction = ManualBalancedMessage();
				return result;
			}

			var options = service.GetType().GetProperty("Options")?.GetValue(service);
			if (options == null)
			{
				result.ErrorCode = ToolErrorCodes.InternalError;
				result.ErrorMessage = "IWorkspaceConfigurationService.Options was null.";
				return result;
			}

			var modeProp = options.GetType().GetProperty("SourceGeneratorExecution");
			if (modeProp == null)
			{
				result.ErrorCode = ToolErrorCodes.InternalError;
				result.ErrorMessage = "SourceGeneratorExecution property missing on workspace configuration options.";
				result.ManualBalancedAction = ManualBalancedMessage();
				return result;
			}

			var modeValue = modeProp.GetValue(options);
			var enumType = modeProp.PropertyType;
			result.SourceGeneratorExecutionEnumType = enumType.AssemblyQualifiedName;
			result.SourceGeneratorExecution = modeValue == null ? null : Enum.GetName(enumType, modeValue);
			result.Evidence["sourceGeneratorExecutionRaw"] = modeValue?.ToString() ?? "<null>";
			result.Evidence["workspacesAssembly"] = workspacesAsm.FullName ?? "";
			result.Evidence["configurationService"] = service.GetType().AssemblyQualifiedName ?? "";

			result.BalancedMode = string.Equals(result.SourceGeneratorExecution, "Balanced", StringComparison.Ordinal);

			if (applyBalanced)
			{
				// Mutation only when a change is necessary; already-Balanced hosts must not fail
				// the gate solely because ISettingsManager is unavailable on this VS version.
				if (!result.BalancedMode)
				{
					result.ErrorMessage = "Experimental workspace must already be in Balanced mode.";
					result.ManualBalancedAction = ManualBalancedMessage();
					modeValue = modeProp.GetValue(options);
					result.SourceGeneratorExecution = modeValue == null ? null : Enum.GetName(enumType, modeValue);
					result.BalancedMode = string.Equals(result.SourceGeneratorExecution, "Balanced", StringComparison.Ordinal);
				}
				else
				{
					result.SettingsKey = SettingsKey;
					result.SettingsAction = "already_balanced_no_mutation";
				}
			}

			result.RequestSucceeded = result.ErrorMessage == null;
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}

	private async Task<Issue70ProbeResult> BuildAsync()
	{
		var result = CreateBase("build");
		try
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
			FillHostIdentity(result);

			var dte = Package.GetGlobalService(typeof(DTE)) as DTE2
				?? throw new InvalidOperationException("DTE2 service unavailable in experimental host.");
			var solutionBuild = dte.Solution.SolutionBuild
				?? throw new InvalidOperationException("SolutionBuild unavailable.");

			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			_buildEvents = dte.Events.BuildEvents;
			_vsBuildEvents_OnBuildBegin = (_, _) =>
			{
				result.BuildStartEvent = DateTimeOffset.UtcNow.ToString("o");
			};
			_vsBuildEvents_OnBuildDone = (_, _) =>
			{
				result.BuildEndEvent = DateTimeOffset.UtcNow.ToString("o");
				tcs.TrySetResult(true);
			};

			_buildEvents.OnBuildBegin += _vsBuildEvents_OnBuildBegin;
			_buildEvents.OnBuildDone += _vsBuildEvents_OnBuildDone;

			try
			{
				result.Evidence["buildQueuedAt"] = DateTimeOffset.UtcNow.ToString("o");
				solutionBuild.Build(false);

				var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(120))) == tcs.Task;
				if (!completed)
				{
					result.BuildTimedOut = true;
					result.ErrorCode = ToolErrorCodes.InternalError;
					result.ErrorMessage = "Solution build timed out after 120 seconds.";
					return result;
				}

				result.LastBuildInfo = solutionBuild.LastBuildInfo;
				result.BuildSucceeded = solutionBuild.LastBuildInfo == 0;
				result.BuildOutput = TryReadBuildOutput(dte);
				result.RequestSucceeded = result.BuildSucceeded;
				if (!result.BuildSucceeded)
				{
					result.ErrorCode = ToolErrorCodes.InternalError;
					result.ErrorMessage = $"Solution build failed. LastBuildInfo={result.LastBuildInfo}.";
				}
			}
			finally
			{
				try
				{
					if (_buildEvents != null)
					{
						if (_vsBuildEvents_OnBuildBegin != null)
							_buildEvents.OnBuildBegin -= _vsBuildEvents_OnBuildBegin;
						if (_vsBuildEvents_OnBuildDone != null)
							_buildEvents.OnBuildDone -= _vsBuildEvents_OnBuildDone;
					}
				}
				finally
				{
					_buildEvents = null;
					_vsBuildEvents_OnBuildBegin = null;
					_vsBuildEvents_OnBuildDone = null;
				}
			}
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}

	private async Task<Issue70ProbeResult> GeneratedDocumentsAsync(string? expectedMemberFullName)
	{
		var result = CreateBase("generated_documents");
		try
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
			FillHostIdentity(result);
			FillWorkspaceCounts(result);

			var ordinaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var d in workspace.CurrentSolution.Projects.SelectMany(p => p.Documents))
			{
				if (!string.IsNullOrEmpty(d.FilePath))
					ordinaryPaths.Add(d.FilePath!);
				ordinaryPaths.Add(d.Name);
			}

			foreach (var project in workspace.CurrentSolution.Projects)
			{
				var generated = await project.GetSourceGeneratedDocumentsAsync();
				foreach (var doc in generated)
				{
					if (!string.IsNullOrEmpty(doc.FilePath) && ordinaryPaths.Contains(doc.FilePath!))
						continue;

					var text = await doc.GetTextAsync();
					var content = text.ToString();
					var info = new Issue70GeneratedDocumentInfo
					{
						HintName = doc.Name,
						FilePath = doc.FilePath,
						ProjectName = project.Name,
						Content = content,
						Sha256 = Sha256(content)
					};
					result.GeneratedDocuments.Add(info);

					if (!string.IsNullOrWhiteSpace(expectedMemberFullName))
					{
						var shortName = expectedMemberFullName!.Split('.').Last();
						if (content.IndexOf(shortName, StringComparison.Ordinal) >= 0 &&
						    content.IndexOf("GeneratedApi", StringComparison.Ordinal) >= 0)
						{
							result.MatchingGeneratedDocument ??= info;
						}
					}
				}
			}

			if (!string.IsNullOrWhiteSpace(expectedMemberFullName) && result.MatchingGeneratedDocument != null)
			{
				var shortName = expectedMemberFullName!.Split('.').Last();
				foreach (var project in workspace.CurrentSolution.Projects)
				{
					var compilation = await project.GetCompilationAsync();
					var symbol = compilation?.GetTypeByMetadataName("Issue70Fixture.GeneratedApi")
						?.GetMembers(shortName).FirstOrDefault();
					if (symbol != null)
					{
						result.MatchingMember = new Issue70MemberHit
						{
							Name = symbol.Name,
							FullName = expectedMemberFullName!,
							MemberType = symbol.Kind.ToString(),
							ContainingSymbol = symbol.ContainingType?.ToDisplayString(),
							ProjectName = project.Name
						};
						break;
					}
				}
			}

			result.RequestSucceeded = true;
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}

	private async Task<Issue70ProbeResult> WorkspaceTextAsync()
	{
		var result = CreateBase("workspace_text");
		try
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
			FillHostIdentity(result);
			FillWorkspaceCounts(result);

			var targets = new[] { "marker.txt", "GeneratedUsage.cs", "ControlApi.cs" };
			foreach (var target in targets)
			{
				Microsoft.CodeAnalysis.TextDocument? textDoc = workspace.CurrentSolution.Projects
					.SelectMany(p => p.Documents)
					.FirstOrDefault(d => string.Equals(Path.GetFileName(d.FilePath ?? d.Name), target, StringComparison.OrdinalIgnoreCase));
				if (textDoc == null)
				{
					textDoc = workspace.CurrentSolution.Projects
						.SelectMany(p => p.AdditionalDocuments)
						.FirstOrDefault(d => string.Equals(Path.GetFileName(d.FilePath ?? d.Name), target, StringComparison.OrdinalIgnoreCase));
				}

				if (textDoc == null)
				{
					result.WorkspaceTexts.Add(new Issue70WorkspaceTextInfo
					{
						RelativePath = target,
						FromWorkspaceDocument = false
					});
					continue;
				}

				var text = await textDoc.GetTextAsync();
				var content = text.ToString();
				result.WorkspaceTexts.Add(new Issue70WorkspaceTextInfo
				{
					RelativePath = target,
					FilePath = textDoc.FilePath,
					Content = content,
					Sha256 = Sha256(content),
					FromWorkspaceDocument = true
				});
			}

			result.RequestSucceeded = true;
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}

	private Issue70ProbeResult CreateBase(string operation)
	{
		var asm = typeof(Issue70ProbeService).Assembly;
		var path = asm.Location;
		return new Issue70ProbeResult
		{
			Operation = operation,
			ProbeVersion = ProbeVersion,
			ProbeAssemblyHash = File.Exists(path) ? Sha256Bytes(File.ReadAllBytes(path)) : "",
			ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id
		};
	}

	private void FillHostIdentity(Issue70ProbeResult result)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
		result.ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
		result.RegistryRoot = dte?.RegistryRoot;
		result.RootSuffix = ExtractRootSuffix(dte?.RegistryRoot);
		result.SolutionFullName = dte?.Solution?.FullName;
	}

	private void FillWorkspaceCounts(Issue70ProbeResult result)
	{
		result.ProjectCount = workspace.CurrentSolution.Projects.Count();
		result.DocumentCount = workspace.CurrentSolution.Projects.SelectMany(p => p.Documents).Count();
		result.WorkspaceReady = result.ProjectCount > 0 && result.DocumentCount > 0;
	}

	private static string? ExtractRootSuffix(string? registryRoot)
	{
		if (string.IsNullOrEmpty(registryRoot)) return null;
		var name = registryRoot!.Split('\\').LastOrDefault() ?? registryRoot;
		if (name.IndexOf("Issue70Exp", StringComparison.OrdinalIgnoreCase) >= 0)
			return "Issue70Exp";
		return name;
	}

	private static string? TryReadBuildOutput(DTE2 dte)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		try
		{
			var pane = dte.ToolWindows.OutputWindow.OutputWindowPanes.Item("Build");
			var doc = pane.TextDocument;
			var edit = doc.CreateEditPoint();
			return edit.GetText(doc.EndPoint);
		}
		catch
		{
			return null;
		}
	}

	private static string ManualBalancedMessage()
		=> "Open Issue70Exp Tools > Options > Text Editor > C# > Advanced and set source generator execution to Balanced.";

	private static Issue70ProbeResult Fail(string message, string code) => new()
	{
		ErrorCode = code,
		ErrorMessage = message,
		RequestSucceeded = false
	};

	private static string Sha256(string content) => Sha256Bytes(Encoding.UTF8.GetBytes(content));

	private static string Sha256Bytes(byte[] bytes)
	{
		using var sha = SHA256.Create();
		var hash = sha.ComputeHash(bytes);
		var sb = new StringBuilder(hash.Length * 2);
		foreach (var b in hash)
			sb.Append(b.ToString("x2"));
		return sb.ToString();
	}
}
