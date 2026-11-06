namespace CodexSessionPulse.Core;

/// <summary>A thread chosen for display and why.</summary>
/// <param name="Thread">The chosen thread.</param>
/// <param name="Selection">The confidence and the reason code.</param>
public sealed record ResolvedThread(ThreadPulseState Thread, ThreadSelection Selection);

/// <summary>
/// Picks the thread to show, always in Auto mode (spec §4.4 items 3–4); there is no manual pin anywhere.
/// </summary>
/// <remarks>
/// <para>Hook signals outrank JSONL: the newest of the user-activity hook and the <c>SessionStart</c>/resume
/// hook wins, user activity first at equal times, and gives <see cref="SelectionConfidence.Confirmed"/>. JSONL
/// activity of another thread never overrides a live hook signal, so background work does not steal the display.
/// Without a live hook thread the main or fork thread with the latest JSONL activity is
/// <see cref="SelectionConfidence.Inferred"/>.</para>
/// <para>Subagents are never chosen. With two or more Codex windows a confirmed choice is capped at inferred,
/// since the foreground window does not prove which tab is selected (§3.3).</para>
/// </remarks>
public sealed class ActiveThreadResolver
{
    private HookSignal? _userActivity;
    private HookSignal? _sessionStart;

    /// <summary>Records a user-activity hook (<c>UserPromptSubmit</c>) for a thread.</summary>
    /// <param name="threadId">The thread the hook belongs to, resolved from <c>transcript_path</c>.</param>
    /// <param name="atUtc">When the hook fired, in UTC.</param>
    public void OnUserActivity(string threadId, DateTimeOffset atUtc)
    {
        _userActivity = Newer(_userActivity, new HookSignal(threadId, atUtc));
    }

    /// <summary>Records a <c>SessionStart</c> or resume hook for a thread.</summary>
    /// <param name="threadId">The thread the hook belongs to, resolved from <c>transcript_path</c>.</param>
    /// <param name="atUtc">When the hook fired, in UTC.</param>
    public void OnSessionStart(string threadId, DateTimeOffset atUtc)
    {
        _sessionStart = Newer(_sessionStart, new HookSignal(threadId, atUtc));
    }

    /// <summary>Chooses the thread to show.</summary>
    /// <param name="registry">The live threads.</param>
    /// <param name="codexWindows">The number of Codex windows found.</param>
    /// <returns>The chosen thread, or <see langword="null"/> when there is none.</returns>
    public ResolvedThread? Resolve(ThreadRegistry registry, int codexWindows)
    {
        ArgumentNullException.ThrowIfNull(registry);

        SelectionConfidence confirmed = codexWindows >= 2 ? SelectionConfidence.Inferred : SelectionConfidence.Confirmed;
        ThreadPulseState? userActivity = Live(registry, _userActivity);
        ThreadPulseState? sessionStart = Live(registry, _sessionStart);

        if (userActivity is not null && (sessionStart is null || _userActivity!.Value.AtUtc >= _sessionStart!.Value.AtUtc))
        {
            return new ResolvedThread(userActivity, new ThreadSelection(confirmed, SelectionReasons.UserActivityHook));
        }

        if (sessionStart is not null)
        {
            return new ResolvedThread(sessionStart, new ThreadSelection(confirmed, SelectionReasons.SessionStartHook));
        }

        ThreadPulseState? latest = null;

        foreach (ThreadPulseState state in registry.Threads)
        {
            if (state.Kind != ThreadKind.Subagent
             && (latest is null
              || state.LastActivityUtc > latest.LastActivityUtc
              || (state.LastActivityUtc == latest.LastActivityUtc
               && string.CompareOrdinal(state.ThreadId, latest.ThreadId) < 0)))
            {
                latest = state;
            }
        }

        return latest is null
            ? null
            : new ResolvedThread(latest, new ThreadSelection(SelectionConfidence.Inferred, SelectionReasons.LatestJsonlActivity));
    }

    private static HookSignal Newer(HookSignal? current, HookSignal candidate)
    {
        return current is { } existing && existing.AtUtc > candidate.AtUtc ? existing : candidate;
    }

    private static ThreadPulseState? Live(ThreadRegistry registry, HookSignal? signal)
    {
        return signal is { } hook
            && registry.TryGet(hook.ThreadId, out ThreadPulseState state)
            && state.Kind != ThreadKind.Subagent
            ? state
            : null;
    }

    private readonly record struct HookSignal(string ThreadId, DateTimeOffset AtUtc);
}
