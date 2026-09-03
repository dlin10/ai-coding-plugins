using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlanForge.Diagnostics;

namespace PlanForge.Run;

/// <summary>
/// Where the connected host says the user is working, asked for once per connection through MCP's
/// roots capability.
/// </summary>
/// <remarks>
/// This is the third job <c>workspaceRoot</c> used to do, and the only one that was never the
/// caller's to choose: a run's own files belong beside the user, while the git window and the
/// workers' working directory belong to the repository under review. The orchestrator picks
/// <c>workspaceRoot</c> from the shape of the task, so on a monorepo it correctly names the
/// repository root and the run's most-read document lands outside the session — where the host
/// cannot linkify it. See issue #53.
/// <para>
/// Measured on 2026-09-03 by answering each host's handshake with a server that records it:
/// claude-code 2.1.258 declares <c>roots: {listChanged: true}</c> and answers with the session's
/// working directory; codex-mcp-client 0.147.0 declares no roots and answers <c>[]</c>; Cursor
/// 1.0.0 declares no roots and answers <c>-32601 Method not found</c>. The declared capability is
/// therefore checked before the request is sent, and a host without it keeps the layout it always
/// had, under <c>workspaceRoot</c>.
/// </para>
/// <para>
/// Roots is deprecated by the specification of 2026-07-28 (SEP-2577, which retires sampling and
/// logging with it) and names no successor: nothing in MCP will tell a server where the user is
/// sitting once it goes. Deprecated features stay functional for a year of spec versions, and the
/// day a host stops declaring the capability this class answers <see langword="null"/> and the run
/// folder goes back under <c>workspaceRoot</c> — the fallback is the migration path, so its removal
/// costs the link rather than the run. MCP9005 is disabled for this file alone so the warning still
/// stands over sampling and logging, which this server does not use and should not start using.
/// </para>
/// </remarks>
#pragma warning disable MCP9005
internal sealed class SessionRoots
{
    /// <summary>
    /// A host that declares the capability and then does not answer would otherwise hold up every
    /// tool call of the run, since each one has to know where the run folder is before it can do
    /// anything. Falling back after five seconds costs a slow host its linkable plan, once.
    /// </summary>
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);

    private readonly McpServer? _server;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _directory;
    private bool _asked;

    public SessionRoots(McpServer server) => _server = server;

    private SessionRoots(string? directory)
    {
        _directory = directory;
        _asked = true;
    }

    /// <summary>A host that declares no roots: every run stays under its own workspace root.</summary>
    public static SessionRoots None { get; } = new((string?)null);

    /// <summary>A host declaring <paramref name="directory"/> and nothing else.</summary>
    public static SessionRoots At(string directory) => new(directory);

    /// <summary>
    /// The host's own directory, or <see langword="null"/> when it declares none. Asked once and
    /// remembered: roots belong to the connection, and every tool call of the run needs the answer.
    /// </summary>
    public async ValueTask<string?> DirectoryAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_asked)
            {
                _directory = await AskAsync(ct).ConfigureAwait(false);
                _asked = true;
            }

            return _directory;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> AskAsync(CancellationToken ct)
    {
        // Two of the three hosts answer a request they never advertised — one with an empty list,
        // one with an error — so the capability is what decides whether to ask at all.
        if (_server?.ClientCapabilities?.Roots is null) return null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(AskTimeout);

        try
        {
            var result = await _server.RequestRootsAsync(new ListRootsRequestParams(), deadline.Token)
                                      .ConfigureAwait(false);
            return FirstDirectory(result.Roots);
        }
        // Our own deadline, not the host taking the call away — that one still has to propagate.
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            return Unavailable(error);
        }
        catch (Exception error) when (error is McpException or InvalidOperationException)
        {
            return Unavailable(error);
        }
    }

    private static string? Unavailable(Exception error)
    {
        RunLog.Current?.Write("warn", "server", "roots.unavailable", ("error", error.Message));
        return null;
    }

    /// <summary>
    /// The first root that names a directory on this machine. The protocol allows several and says
    /// nothing about their order; the hosts that declare any declare one, and a root we cannot
    /// resolve to an existing directory is worth skipping rather than failing over.
    /// </summary>
    internal static string? FirstDirectory(IEnumerable<Root> roots)
    {
        foreach (var root in roots)
        {
            if (Uri.TryCreate(root.Uri, UriKind.Absolute, out var uri) && uri.IsFile
                                                                      && Directory.Exists(uri.LocalPath))
                return Path.GetFullPath(uri.LocalPath);
        }

        return null;
    }
}
#pragma warning restore MCP9005
