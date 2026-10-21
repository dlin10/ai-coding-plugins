namespace PlanForge.Orchestration;

/// <summary>
/// The one answer, per server process, to what the Host session is (R5): with MCP client
/// <c>claude-code</c>, the process's <c>CLAUDE_CODE_SESSION_ID</c>; otherwise the latest
/// <c>threadId</c> a call carried in its <c>_meta</c>; otherwise none.
/// </summary>
/// <remarks>
/// The environment is read through the injected reader and the latest thread is kept here, so a
/// test creates its own instance and never reads the real variable.
/// </remarks>
internal sealed class HostSessions
{
    internal const string CLAUDE_CODE = "claude-code";
    internal const string CODEX = "codex";

    internal const string CLAUDE_SESSION_VARIABLE = "CLAUDE_CODE_SESSION_ID";

    private readonly Func<string, string?> _environment;
    private readonly object _gate = new();
    private string? _client;
    private string? _threadId;

    /// <summary>Creates an owner that reads the environment through <paramref name="environment"/>.</summary>
    /// <param name="environment">Reads the server process's environment variables.</param>
    public HostSessions(Func<string, string?> environment) => _environment = environment;

    /// <summary>The MCP client's name, as the latest call that knew it gave it.</summary>
    public string? Client
    {
        get { lock (_gate) return _client; }
    }

    /// <summary>Notes what a call says about its host: the client's name, and the Codex thread it carries.</summary>
    /// <param name="client">The MCP client's name, if the call knows it.</param>
    /// <param name="threadId">The call's <c>_meta.threadId</c>, if it carries one.</param>
    public void Observe(string? client, string? threadId)
    {
        lock (_gate)
        {
            if (client is { Length: > 0 }) _client = client;
            if (threadId is { Length: > 0 }) _threadId = threadId;
        }
    }

    /// <summary>The Host session as things stand: Claude Code's session id, else the latest Codex thread, else null.</summary>
    public HostSession? Current()
    {
        string? client, threadId;
        lock (_gate)
        {
            client = _client;
            threadId = _threadId;
        }

        if (client == CLAUDE_CODE)
            return _environment(CLAUDE_SESSION_VARIABLE) is { Length: > 0 } id ? new HostSession(CLAUDE_CODE, id) : null;

        return threadId is not null ? new HostSession(CODEX, threadId) : null;
    }
}
