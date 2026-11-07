using System.Text.RegularExpressions;

namespace CodexSessionPulse.Infrastructure.Rollouts;

/// <summary>
/// Reads the thread id out of a rollout file name:
/// <c>rollout-&lt;yyyy-mm-ddThh-mm-ss&gt;-&lt;thread-id&gt;[_&lt;rollout-id&gt;].jsonl[.zst]</c>. The <c>_&lt;rollout-id&gt;</c>
/// suffix marks a later segment of the same thread (compatibility.md §7.4).
/// </summary>
public static partial class RolloutFileName
{
    /// <summary>Gets the thread id from a rollout path, e.g. a hook's <c>transcript_path</c>.</summary>
    /// <param name="path">The rollout path or file name.</param>
    /// <param name="threadId">The thread id when the name matches.</param>
    /// <returns>Whether the name is a rollout name.</returns>
    public static bool TryGetThreadId(string? path, out string threadId)
    {
        Match match = path is null ? Match.Empty : NamePattern().Match(Path.GetFileName(path));
        threadId = match.Success ? match.Groups["thread"].Value : string.Empty;

        return match.Success;
    }

    [GeneratedRegex(@"^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<thread>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})(?:_[0-9A-Fa-f-]{36})?\.jsonl(?:\.zst)?$",
                    RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
