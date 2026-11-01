using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Codex;

/// <summary>
/// One codex conversation, driven through `codex exec` rather than the App Server — see
/// docs/adr/0012-reach-codex-through-exec.md. Modelled on <see cref="Claude.ClaudeCliSession"/>:
/// one process per turn, JSONL on stdout, structure demanded rather than negotiated.
/// </summary>
internal sealed partial class CodexCliSession : IVendorSession
{
    internal const string SELF_PLUGIN_DISABLE ="plugins.plan-forge-flow@dlin10-ai-coding-plugins.enabled=false";

    /// <summary>
    /// How codex reports a command it could not start, pinned by codex's own
    /// <c>unified_exec_launch_failure_tests.rs</c>: status <c>failed</c>, exit code -1, and output that
    /// opens with this text. Measured against codex-cli 0.162.1 on 2026-10-12, when a failed sandbox
    /// setup kept every command from starting. See CONTEXT.md and docs/adr/0031.
    /// </summary>
    private const string LAUNCH_FAILURE = "Failed to create unified exec process:";

    private const int QUOTE_LIMIT = 400;

    private readonly RoleSpec _role;
    private readonly Selection _selection;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyList<CodexServer> _grantedServers;
    private readonly Channel<VendorEvent> _events = Channel.CreateUnbounded<VendorEvent>();

    // Set on the first run so a resumable role's later calls resume the same thread.
    private string? _sessionId;

    // Names an API refusal when the run ends with no result to show for it.
    private string? _lastFailure;
    private bool _turnFailed;
    private JsonElement? _terminalUsage;
    private string? _attemptSessionId;

    // Per run: the commands codex started, the ones it could not, and what it said about the first
    // of those. A run that tried commands and started none read nothing through its shell.
    private int _commandsStarted;
    private int _commandsUnstarted;
    private string? _launchFailure;

    /// <summary>Prepares one codex conversation; no process starts until its first turn.</summary>
    /// <param name="role">The worker role and its contract.</param>
    /// <param name="selection">The selected model and effort.</param>
    /// <param name="workingDirectory">The workspace the worker runs in.</param>
    /// <param name="resumeToken">The builder or Scout thread to resume, when one exists.</param>
    /// <param name="grantedServers">The MCP servers this worker may call unasked, as codex lists them.</param>
    public CodexCliSession(RoleSpec role,
                           Selection selection,
                           string? workingDirectory,
                           string? resumeToken = null,
                           IReadOnlyList<CodexServer>? grantedServers = null)
    {
        _role = role;
        _selection = selection;
        _workingDirectory = workingDirectory;
        _sessionId = resumeToken;
        _grantedServers = grantedServers ?? [];
    }

    public IAsyncEnumerable<VendorEvent> Events => _events.Reader.ReadAllAsync();

    public bool CanResume => _role.Role is VendorRole.Builder or VendorRole.Scout;

    public string? ResumeToken => CanResume ? _sessionId : null;

    /// <summary>The attempt's own usage: what it added to the thread total the thread last reported.</summary>
    internal WorkerUsage UsageSince(WorkerUsage? previous) => ProviderUsage.Codex(_terminalUsage, previous);

    public async Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
    {
        _lastFailure = null;
        _turnFailed = false;
        _terminalUsage = null;
        _attemptSessionId = null;
        _commandsStarted = 0;
        _commandsUnstarted = 0;
        _launchFailure = null;
        var resumeToken = _sessionId;
        var command = CodexLaunch.Command;
        var directory = Path.Combine(Path.GetTempPath(), "planforge-codex", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var schemaPath = Path.Combine(directory, "schema.json");
        var resultPath = Path.Combine(directory, "result.json");

        try
        {
            await File.WriteAllTextAsync(schemaPath, schema.Json, ct);

            var inspected = CodexLaunch.Inspect(Environment.GetEnvironmentVariable("PATH"));
            var environment = inspected.Repaired
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = inspected.Path! }
                : null;

            var arguments = BuildArguments(_role, _selection, _sessionId, schemaPath, resultPath, _grantedServers);
            var spec = command.CreateProcess(arguments, _workingDirectory, prompt, environment);

            await _events.Writer.EmitAsync("codex", new VendorEvent(VendorEventKind.Started, _selection.Model), ct);
            var turn = new VendorTurn(_role, _selection, "codex");
            var attempt = turn.Start(VendorTurn.PromptBytes(_role.SystemPrompt, prompt, schema.Json), resumeToken);

            try
            {
                await foreach (var line in StreamingProcess.RunWorkerAsync(spec, ct, attempt))
                {
                    if (!TryParse(line, out var document)) continue;
                    using (document) Observe(document.RootElement, attempt);
                }

                var noShell = JudgeShell();

                if (_turnFailed)
                {
                    attempt.Failed();
                    throw new VendorException(NoResultMessage);
                }

                if (noShell is not null)
                {
                    attempt.Failed();
                    throw noShell;
                }

                if (!File.Exists(resultPath))
                {
                    attempt.InvalidOutput();
                    throw new VendorException(NoResultMessage);
                }

                T? result;
                try
                {
                    result = JsonSerializer.Deserialize(await File.ReadAllTextAsync(resultPath, ct), schema.TypeInfo);
                }
                catch (JsonException)
                {
                    attempt.InvalidOutput();
                    throw;
                }

                if (result is null)
                {
                    attempt.InvalidOutput();
                    throw new VendorException(NoResultMessage);
                }

                attempt.Succeeded();
                await _events.Writer.EmitAsync("codex", new VendorEvent(VendorEventKind.Finished, _role.Role.ToString()), ct);
                return result;
            }
            catch (OperationCanceledException)
            {
                attempt.Cancelled();
                throw;
            }
            finally
            {
                attempt.Finish(UsageSince, _attemptSessionId);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Why a run ended without a result, as far as the stream said. Internal for the tests.</summary>
    internal string NoResultMessage => _lastFailure is { Length: > 0 }
        ? $"codex wrote no result: {_lastFailure}"
        : "codex wrote no result";

    /// <summary>
    /// The failure to report when the run tried commands and codex started none of them, or null
    /// when it started one or tried none. A codex Worker reads the repository through its shell, so
    /// such a run answered blind — in run 20261012-163837-6fc7b1 every Critic and Scout did, behind a
    /// sandbox setup that failed before each command. Internal so the tests can drive it after
    /// <see cref="Observe(JsonElement)"/>.
    /// </summary>
    internal NoShellException? JudgeShell()
    {
        if (_commandsStarted > 0 || _launchFailure is null) return null;

        var at = DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        var diagnosis = $"codex could not start a shell for the {_role.Role} at {at}, so its answer was not used: "
                        + $"none of the {_commandsUnstarted} command(s) it tried started — \"{_launchFailure}\".";

        return new NoShellException(diagnosis, "Retry once codex can start a command.", ErrorCode(_launchFailure));
    }

    /// <summary>
    /// The error code a launch diagnostic carries after its fixed opening —
    /// <c>helper_unknown_error</c> in "Failed to create unified exec process: helper_unknown_error:
    /// setup refresh had errors" — or null when what follows is not a bare identifier.
    /// </summary>
    /// <param name="diagnostic">A launch diagnostic, as <see cref="IsLaunchFailure"/> returned it.</param>
    internal static string? ErrorCode(string diagnostic)
    {
        if (!diagnostic.StartsWith(LAUNCH_FAILURE, StringComparison.Ordinal)) return null;

        var rest = diagnostic[LAUNCH_FAILURE.Length..].TrimStart();
        var end = rest.IndexOf(':');
        var candidate = end < 0 ? rest : rest[..end];
        return Identifier().IsMatch(candidate) ? candidate : null;
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The prompt travels on standard input, never as an argument: a code-review prompt carries a
    /// whole diff and would exceed the Windows command-line limit. Static and internal so a test
    /// can pin the exact order without starting a process.
    /// </summary>
    /// <param name="role">The worker role and its contract.</param>
    /// <param name="selection">The selected model and effort.</param>
    /// <param name="sessionId">The builder or Scout session to resume, when one exists.</param>
    /// <param name="schemaPath">The output-schema file path.</param>
    /// <param name="resultPath">The file where Codex writes the final result.</param>
    /// <param name="grantedServers">The MCP servers to approve, each one codex listed.</param>
    internal static List<string> BuildArguments(RoleSpec role,
                                                 Selection selection,
                                                 string? sessionId,
                                                 string schemaPath,
                                                 string resultPath,
                                                 IReadOnlyList<CodexServer>? grantedServers = null)
    {
        var arguments = new List<string> { "exec" };

        if (role.Role is (VendorRole.Builder or VendorRole.Scout) && !string.IsNullOrEmpty(sessionId))
        {
            arguments.Add("resume");
            arguments.Add(sessionId);
        }

        // A critic is deliberately fresh every round. Keeping its rollout would only add a
        // resumable session to the host's history; a builder and Scout must keep their rollouts for
        // later calls.
        if (role.Role is VendorRole.Critic) arguments.Add("--ephemeral");

        arguments.Add("-");
        arguments.Add("--skip-git-repo-check");
        arguments.Add("--json");
        arguments.Add("--output-schema");
        arguments.Add(schemaPath);
        arguments.Add("-o");
        arguments.Add(resultPath);
        arguments.Add("-m");
        arguments.Add(selection.Model);

        if (!string.IsNullOrWhiteSpace(selection.Effort))
        {
            arguments.Add("-c");
            arguments.Add("model_reasoning_effort=" + TomlValue.String(selection.Effort));
        }

        // Standard speed is named as explicitly as Fast: without it a worker would inherit the tier
        // `~/.codex/config.toml` holds, which the Codex desktop app rewrites from its Fast toggle.
        // See docs/adr/0023.
        arguments.Add("-c");
        arguments.Add("service_tier=" + TomlValue.String(selection.Fast ? "fast" : "default"));

        // A worker may inherit every other host capability, but never this plugin: exposing forge
        // inside forge lets either role start a run within its parent run. Disable the whole plugin
        // rather than only its MCP server so its skill and instructions are absent as well.
        arguments.Add("-c");
        arguments.Add(SELF_PLUGIN_DISABLE);

        // The user's notes across sessions change while a run is live: a worker that reads them is
        // steered by input the run does not record, and one that writes them leaves a change neither
        // the diff nor code review sees (issue #233). `--disable memories` is the same as
        // `-c features.memories=false`, and `codex exec resume` takes it as `exec` does. AGENTS.md
        // stays: it is the workers' standing context.
        arguments.Add("--disable");
        arguments.Add("memories");

        // Every role uses the launching process's permissions on fresh and resumed turns; Critic and
        // Scout stay read-only by their contracts alone. See docs/adr/0028 and docs/adr/0033.
        arguments.Add("--dangerously-bypass-approvals-and-sandbox");

        // `codex exec` has nobody to approve an MCP call, so one nothing approved fails (issue #90).
        // The key is per server and merges into the server codex already knows; naming one it does
        // not know stops codex before it starts, so only servers its own list reported get here. A
        // plugin's server is known only under its plugin: the top-level key would invent another.
        foreach (var server in grantedServers ?? [])
        {
            var table = server.Plugin is { } plugin ? $"plugins.{plugin}.mcp_servers" : "mcp_servers";
            arguments.Add("-c");
            arguments.Add($"{table}.{server.Name}.default_tools_approval_mode=" + TomlValue.String("approve"));
        }

        // Sent on every turn, including a resumed one: a resumed turn keeps the instructions its
        // thread started with, so this is harmless rather than effective, and forge never changes a
        // role mid-session.
        arguments.Add("-c");
        arguments.Add("developer_instructions=" + TomlValue.String(role.SystemPrompt));

        return arguments;
    }

    /// <summary>
    /// Turns one line of `codex exec --json` into events. Internal so the tests can drive it
    /// directly, as they already drive the Claude session.
    /// </summary>
    /// <param name="root">The parsed JSONL message.</param>
    internal void Observe(JsonElement root) => Observe(root, null);

    /// <summary>Reads one stream message and measures its tool boundaries for the current attempt.</summary>
    /// <param name="root">The parsed JSONL message.</param>
    /// <param name="attempt">The process attempt to measure, or null when only reading events.</param>
    internal void Observe(JsonElement root, VendorAttempt? attempt)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            RunLog.Current?.Write("warn", "codex", "vendor.skipped-line",
                ("payload", RunLog.Truncate(root.GetRawText())));
            return;
        }

        if (!TryRead(root, "type", out var type)) return;

        switch (type.GetString())
        {
            case "thread.started":
                if (TryRead(root, "thread_id", out var threadId) && threadId.GetString() is { } id)
                {
                    _attemptSessionId = id;
                    _sessionId = id;
                }
                break;

            case "turn.completed":
                if (root.TryGetProperty("usage", out var completedUsage)) _terminalUsage = completedUsage.Clone();
                break;

            case "item.started":
                if (TryItem(root, "command_execution", out var startedItem))
                {
                    attempt?.ToolStarted(TryRead(startedItem, "id", out var itemId) && itemId.ValueKind is JsonValueKind.String
                        ? itemId.GetString() : null);
                    List<(string Name, string? Value)>? fields = null;
                    if (TryRead(startedItem, "command", out var command) && command.GetString() is { } commandText)
                        (fields ??= []).Add(("command", commandText));

                    _events.Writer.Emit("codex", new VendorEvent(VendorEventKind.ToolUse, "command_execution", fields));
                }
                else if (TryItem(root, "mcp_tool_call", out var startedCall))
                {
                    attempt?.ToolStarted(TryRead(startedCall, "id", out var callId) && callId.ValueKind is JsonValueKind.String
                        ? callId.GetString() : null);
                    List<(string Name, string? Value)>? fields = null;
                    if (TryRead(startedCall, "arguments", out var callArguments))
                        (fields ??= []).Add(("input", RunLog.Truncate(callArguments.GetRawText())));

                    _events.Writer.Emit("codex", new VendorEvent(VendorEventKind.ToolUse, McpToolName(startedCall), fields));
                }
                break;

            case "item.completed":
                if (TryItem(root, "command_execution", out var completedItem))
                {
                    attempt?.ToolCompleted(TryRead(completedItem, "id", out var itemId) && itemId.ValueKind is JsonValueKind.String
                        ? itemId.GetString() : null);
                    if (IsLaunchFailure(completedItem, out var launchFailure))
                    {
                        _commandsUnstarted++;
                        _launchFailure ??= launchFailure;
                    }
                    else
                    {
                        _commandsStarted++;
                    }
                    _events.Writer.Emit("codex",
                        new VendorEvent(VendorEventKind.ToolResult, "command_execution", CommandExecutionDetail(completedItem)));
                }
                else if (TryItem(root, "mcp_tool_call", out var completedCall))
                {
                    attempt?.ToolCompleted(TryRead(completedCall, "id", out var callId) && callId.ValueKind is JsonValueKind.String
                        ? callId.GetString() : null);
                    _events.Writer.Emit("codex",
                        new VendorEvent(VendorEventKind.ToolResult, McpToolName(completedCall), McpToolCallDetail(completedCall)));
                }
                else if (TryItem(root, "agent_message", out var messageItem)
                    && TryRead(messageItem, "text", out var text) && text.GetString() is { } message)
                {
                    _events.Writer.Emit("codex", new VendorEvent(VendorEventKind.Text, message));
                }
                // codex 0.154.0 turns its warning, config-warning, deprecation and model-reroute
                // notices into an item of type `error`. None of them ends the turn.
                else if (TryItem(root, "error", out var noticeItem)
                    && TryRead(noticeItem, "message", out var notice) && notice.GetString() is { } noticeText)
                {
                    Warn(noticeText);
                }
                break;

            case "turn.failed":
                _turnFailed = true;
                if (root.TryGetProperty("usage", out var failedUsage)) _terminalUsage = failedUsage.Clone();
                if (TryRead(root, "error", out var turnError) && TryRead(turnError, "message", out var turnMessage)
                    && turnMessage.GetString() is { } turnReason)
                {
                    _lastFailure = turnReason;
                    _events.Writer.Emit("codex", new VendorEvent(VendorEventKind.Failed, turnReason));
                }
                break;

            // Not a failure by itself: codex 0.154.0 drops the server's `will_retry`, so a retried
            // stream error looks the same as a fatal one, and a healthy run was measured carrying the
            // skill-budget notice here. A fatal error is always followed by `turn.failed`, which
            // repeats the last error's message when the turn has none, so that is the failure.
            case "error":
                if (TryRead(root, "message", out var errorMessage) && errorMessage.GetString() is { } reason)
                    Warn(reason);
                break;
        }
    }

    private static void Warn(string text) =>
        RunLog.Current?.Write("warn", "codex", "vendor.warning", ("text", text));

    /// <summary>The command's fields, carried only when their property is present.</summary>
    /// <param name="item">The completed command-execution item.</param>
    private static List<(string Name, string? Value)>? CommandExecutionDetail(JsonElement item)
    {
        List<(string Name, string? Value)>? detail = null;

        void Carry(string name, string? value)
        {
            if (value is null) return;
            (detail ??= []).Add((name, value));
        }

        if (TryRead(item, "command", out var command)) Carry("command", command.GetString());
        if (TryRead(item, "exit_code", out var exitCode) && exitCode.ValueKind is JsonValueKind.Number)
            Carry("exitCode", exitCode.GetRawText());
        if (TryRead(item, "aggregated_output", out var output) && output.GetString() is { } text)
            Carry("output", RunLog.Tail(text));
        if (TryRead(item, "status", out var status)) Carry("status", status.GetString());

        return detail;
    }

    /// <summary>
    /// Whether a completed <c>command_execution</c> item is a command codex could not start, and if so
    /// what codex said instead of its output.
    /// </summary>
    /// <param name="item">A completed <c>command_execution</c> item.</param>
    /// <param name="diagnostic">Receives codex's launch diagnostic, on one line and bounded.</param>
    private static bool IsLaunchFailure(JsonElement item, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (item.ValueKind is not JsonValueKind.Object) return false;

        if (!item.TryGetProperty("status", out var status) || status.ValueKind is not JsonValueKind.String
            || status.GetString() is not "failed")
            return false;

        if (!item.TryGetProperty("exit_code", out var exitCode) || exitCode.ValueKind is not JsonValueKind.Number
            || !exitCode.TryGetInt32(out var code) || code != -1)
            return false;

        if (!item.TryGetProperty("aggregated_output", out var output) || output.ValueKind is not JsonValueKind.String
            || output.GetString() is not { } text || !text.StartsWith(LAUNCH_FAILURE, StringComparison.Ordinal))
            return false;

        diagnostic = Quote(text);
        return true;
    }

    /// <summary>One line, bounded, so a quoted diagnostic cannot carry a whole command's output.</summary>
    /// <param name="text">The diagnostic as codex wrote it.</param>
    private static string Quote(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= QUOTE_LIMIT ? line : line[..QUOTE_LIMIT] + "…";
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex Identifier();

    /// <summary>
    /// An MCP call named the way claude names its tools, so one grep finds a Roslyn call in either
    /// vendor's log. Measured against codex-cli 0.154.0: the item carries `server` and `tool`.
    /// </summary>
    /// <param name="item">A <c>mcp_tool_call</c> item.</param>
    private static string McpToolName(JsonElement item)
    {
        var server = TryRead(item, "server", out var serverName) ? serverName.GetString() : null;
        var tool = TryRead(item, "tool", out var toolName) ? toolName.GetString() : null;
        return $"mcp__{server ?? "?"}__{tool ?? "?"}";
    }

    /// <summary>
    /// The outcome of an MCP call. A refusal is not an exit code but a `failed` status beside an
    /// `error.message` — "MCP tool call requires approval, but approval policy is never" for a
    /// server nothing approved — and the process still exits 0.
    /// </summary>
    /// <param name="item">A completed <c>mcp_tool_call</c> item.</param>
    private static List<(string Name, string? Value)> McpToolCallDetail(JsonElement item)
    {
        var status = TryRead(item, "status", out var statusValue) ? statusValue.GetString() : null;
        var detail = new List<(string Name, string? Value)>
        {
            ("isError", status is "completed" ? "false" : "true"),
            ("status", status)
        };

        if (TryRead(item, "error", out var error) && TryRead(error, "message", out var message) && message.GetString() is { } refusal)
            detail.Add(("error", refusal));
        if (TryRead(item, "result", out var result) && result.ValueKind is not JsonValueKind.Null)
            detail.Add(("output", RunLog.Tail(result.GetRawText())));

        return detail;
    }

    private static bool TryItem(JsonElement root, string type, out JsonElement item)
    {
        if (TryRead(root, "item", out var candidate)
            && TryRead(candidate, "type", out var itemType) && itemType.GetString() == type)
        {
            item = candidate;
            return true;
        }

        item = default;
        return false;
    }

    /// <summary>
    /// A property read that survives a value of the wrong kind, mirroring
    /// <see cref="Claude.ClaudeCliSession"/>'s helper of the same name: a shape nobody predicted
    /// must cost a skipped block, not the run.
    /// </summary>
    /// <param name="element">The JSON value that may be an object.</param>
    /// <param name="name">The property name to read.</param>
    /// <param name="value">Receives the property value when present.</param>
    private static bool TryRead(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind is JsonValueKind.Object) return element.TryGetProperty(name, out value);

        value = default;
        return false;
    }

    private static bool TryParse(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
