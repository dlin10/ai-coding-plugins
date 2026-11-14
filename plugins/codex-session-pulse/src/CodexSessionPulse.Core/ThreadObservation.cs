namespace CodexSessionPulse.Core;

/// <summary>A thread seen in a rollout's <c>session_meta</c>, before or without any usage event.</summary>
/// <param name="ThreadId">The thread id (<c>session_meta.id</c>, or the id in the file name when the meta is unreadable).</param>
/// <param name="Kind">The kind of the thread.</param>
/// <param name="ParentThreadId">The parent thread of a fork or subagent, when known.</param>
/// <param name="RootSessionId">The <c>session_meta.session_id</c>, when known; never a key.</param>
/// <param name="SourcePath">The rollout file.</param>
/// <param name="CliVersion">The <c>session_meta.cli_version</c>, when known.</param>
/// <param name="ObservedAtUtc">The time of the <c>session_meta</c> record, in UTC.</param>
public sealed record ThreadObservation(string ThreadId,
                                      ThreadKind Kind,
                                      string? ParentThreadId,
                                      string? RootSessionId,
                                      string SourcePath,
                                      string? CliVersion,
                                      DateTimeOffset ObservedAtUtc);
