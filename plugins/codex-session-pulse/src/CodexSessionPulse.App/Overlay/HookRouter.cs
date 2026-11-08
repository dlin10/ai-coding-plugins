using CodexSessionPulse.App.Ipc;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.App.Overlay;

/// <summary>
/// Routes hook messages into the engine and remembers the last one that selected a thread, so that an engine recreated
/// by a settings save (a new rollouts root, TTL or minimum) starts with the hook-confirmed selection instead of losing it
/// until the next hook. Only <c>SessionStart</c>, <c>UserPromptSubmit</c> and <c>SessionEnd</c> with a thread id reach the
/// engine; a <c>SessionEnd</c> of the remembered thread forgets it. Thread-safe: hooks arrive on the thread pool, a
/// settings save replays on the UI thread.
/// </summary>
internal sealed class HookRouter
{
    private readonly Lock _gate = new();
    private Selection? _last;

    /// <summary>Applies a hook to the engine, if there is one, and updates the remembered selection either way.</summary>
    /// <param name="engine">The current engine, or <see langword="null"/> while the pipeline restarts.</param>
    /// <param name="message">The hook message.</param>
    /// <param name="atUtc">When the hook happened, already checked against the overlay's clock.</param>
    public void Route(PulseEngine? engine, HookMessage message, DateTimeOffset atUtc)
    {
        if (message.ThreadId is not { } threadId)
        {
            return;
        }

        lock (_gate)
        {
            switch (message.Event)
            {
                case "SessionStart":
                case "UserPromptSubmit":
                    _last = new Selection(threadId, message.Event, atUtc);
                    break;
                case "SessionEnd" when _last?.ThreadId == threadId:
                    _last = null;
                    break;
            }
        }

        if (engine is not null)
        {
            Apply(engine, threadId, message.Event, atUtc);
        }
    }

    /// <summary>Gives a new engine the last hook-confirmed selection, with its original time.</summary>
    /// <param name="engine">The engine that replaced the previous one.</param>
    public void Replay(PulseEngine engine)
    {
        Selection? last;

        lock (_gate)
        {
            last = _last;
        }

        if (last is not null)
        {
            Apply(engine, last.ThreadId, last.Event, last.AtUtc);
        }
    }

    private static void Apply(PulseEngine engine, string threadId, string hookEvent, DateTimeOffset atUtc)
    {
        switch (hookEvent)
        {
            case "SessionStart":
                engine.OnSessionStart(threadId, atUtc);
                break;
            case "UserPromptSubmit":
                engine.OnUserActivity(threadId, atUtc);
                break;
            case "SessionEnd":
                engine.OnSessionEnd(threadId, atUtc);
                break;
        }
    }

    /// <summary>The last hook that selected a thread.</summary>
    /// <param name="ThreadId">The thread.</param>
    /// <param name="Event">The <c>hook_event_name</c>: <c>SessionStart</c> or <c>UserPromptSubmit</c>.</param>
    /// <param name="AtUtc">When the hook happened.</param>
    private sealed record Selection(string ThreadId, string Event, DateTimeOffset AtUtc);
}
