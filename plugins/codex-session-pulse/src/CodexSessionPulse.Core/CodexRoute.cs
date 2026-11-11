namespace CodexSessionPulse.Core;

/// <summary>What a <see cref="CodexRoute"/> shows in Codex Desktop's main view.</summary>
public enum CodexRouteKind
{
    /// <summary>A local thread: the one whose rollout carries <see cref="CodexRoute.ThreadId"/>.</summary>
    LocalThread,

    /// <summary>The New chat page: no thread is open.</summary>
    NewChat,

    /// <summary>A page with no rollout on this computer: a remote host's thread, a ChatGPT project chat or a cloud task.</summary>
    Other,
}

/// <summary>
/// The page Codex Desktop opened, as its own log names it. Unlike a hook, it is written on every switch, a passive one
/// included (a click in the sidebar without a prompt), so it is the one signal that says which thread is on screen
/// (docs/compatibility.md §6.3).
/// </summary>
/// <param name="Kind">What the page shows.</param>
/// <param name="ThreadId">The thread id for <see cref="CodexRouteKind.LocalThread"/>; otherwise <see langword="null"/>.</param>
/// <param name="AtUtc">When Codex logged the switch, in UTC.</param>
public sealed record CodexRoute(CodexRouteKind Kind, string? ThreadId, DateTimeOffset AtUtc);
