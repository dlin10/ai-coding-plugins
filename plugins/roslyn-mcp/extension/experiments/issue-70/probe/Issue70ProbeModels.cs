using System.Collections.Generic;

namespace RoslynMcpExtension.Shared;

// ISSUE70: temporary probe models — remove in task 4.
public sealed class Issue70ProbeResult : IToolResult
{
	public bool RequestSucceeded { get; set; }
	public string? ErrorCode { get; set; }
	public string? ErrorMessage { get; set; }

	public string Operation { get; set; } = string.Empty;
	public int ProcessId { get; set; }
	public string? RegistryRoot { get; set; }
	public string? RootSuffix { get; set; }
	public string? SolutionFullName { get; set; }
	public bool WorkspaceReady { get; set; }
	public int ProjectCount { get; set; }
	public int DocumentCount { get; set; }
	public string? ProbeVersion { get; set; }
	public string? ProbeAssemblyHash { get; set; }

	public string? SourceGeneratorExecution { get; set; }
	public string? WorkspaceConfigurationServiceType { get; set; }
	public string? WorkspacesAssemblyIdentity { get; set; }
	public string? SourceGeneratorExecutionEnumType { get; set; }
	public bool BalancedMode { get; set; }
	public string? SettingsKey { get; set; }
	public bool? SettingsKeyExisted { get; set; }
	public string? SettingsPriorValue { get; set; }
	public string? SettingsAppliedValue { get; set; }
	public string? SettingsAction { get; set; }
	public string? ManualBalancedAction { get; set; }

	public bool BuildSucceeded { get; set; }
	public int? LastBuildInfo { get; set; }
	public bool BuildTimedOut { get; set; }
	public string? BuildOutput { get; set; }
	public string? BuildStartEvent { get; set; }
	public string? BuildEndEvent { get; set; }

	public List<Issue70GeneratedDocumentInfo> GeneratedDocuments { get; set; } = [];
	public Issue70GeneratedDocumentInfo? MatchingGeneratedDocument { get; set; }
	public Issue70MemberHit? MatchingMember { get; set; }

	public List<Issue70WorkspaceTextInfo> WorkspaceTexts { get; set; } = [];
	public Dictionary<string, string> Evidence { get; set; } = new();
}

public sealed class Issue70GeneratedDocumentInfo
{
	public string HintName { get; set; } = string.Empty;
	public string? FilePath { get; set; }
	public string Sha256 { get; set; } = string.Empty;
	public string Content { get; set; } = string.Empty;
	public string ProjectName { get; set; } = string.Empty;
}

public sealed class Issue70MemberHit
{
	public string Name { get; set; } = string.Empty;
	public string FullName { get; set; } = string.Empty;
	public string MemberType { get; set; } = string.Empty;
	public string? ContainingSymbol { get; set; }
	public string? ProjectName { get; set; }
}

public sealed class Issue70WorkspaceTextInfo
{
	public string RelativePath { get; set; } = string.Empty;
	public string? FilePath { get; set; }
	public string Sha256 { get; set; } = string.Empty;
	public string Content { get; set; } = string.Empty;
	public bool FromWorkspaceDocument { get; set; }
}
