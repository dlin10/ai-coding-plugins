using System.Text.RegularExpressions;
using PlanForge.Diagnostics;
using PlanForge.Run;

namespace PlanForge.Vendors;

/// <summary>
/// The MCP servers a worker may call without being asked. A headless worker has nobody to answer a
/// permission prompt, so whatever its vendor does not already allow is refused (issue #90). The run
/// names servers by pattern, and each launch matches the patterns against the vendor's own server
/// list, because neither vendor takes a pattern: claude matches a rule on the exact server name,
/// codex approves one server at a time. See docs/adr/0017.
/// </summary>
internal static class WorkerTools
{
    private const string SOURCE = "worker";

    // How claude lists a plugin's server: `plugin:<plugin>:<server>`.
    private const string PLUGIN_PREFIX = "plugin:";

    /// <summary>What a run begun without <c>workerTools</c> grants: the roslyn-mcp servers, however a repository named them.</summary>
    public static IReadOnlyList<string> Default { get; } = ["roslyn-*"];

    /// <summary>The run's patterns, where a run that named none — or predates the setting — gets <see cref="Default"/>.</summary>
    public static IReadOnlyList<string> Effective(IReadOnlyList<string>? patterns) => patterns ?? Default;

    /// <summary>Refuses a pattern that could never name a server; an empty list is a choice and stays one.</summary>
    public static IReadOnlyList<string>? Validate(IReadOnlyList<string>? patterns)
    {
        foreach (var pattern in patterns ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Any(char.IsWhiteSpace))
                throw new ArgumentRejectedException($"workerTools names MCP servers, and '{pattern}' cannot be a server name");
        }

        return patterns;
    }

    /// <summary>
    /// The servers any pattern matches, in list order, each once; <c>*</c> stands for any run of
    /// characters. A pattern matches the whole name, or the server part of a name claude lists as
    /// <c>plugin:&lt;plugin&gt;:&lt;server&gt;</c>, so it names a server by its key in either vendor —
    /// codex lists a plugin's server by its key alone. The plugin part alone never matches.
    /// </summary>
    /// <param name="patterns">The run's server-name patterns.</param>
    /// <param name="servers">The server names the vendor listed.</param>
    public static IReadOnlyList<string> Match(IReadOnlyList<string> patterns, IEnumerable<string> servers)
    {
        var expressions = patterns.Select(pattern => new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$",
                                                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                                  .ToList();

        return [.. servers.Where(server => expressions.Any(expression => expression.IsMatch(server)
                                                                       || expression.IsMatch(ServerKey(server))))
                          .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The <c>&lt;server&gt;</c> of a <c>plugin:&lt;plugin&gt;:&lt;server&gt;</c> name, or the name itself.</summary>
    /// <param name="server">A server name as the vendor lists it.</param>
    private static string ServerKey(string server)
    {
        if (!server.StartsWith(PLUGIN_PREFIX, StringComparison.Ordinal)) return server;

        var separator = server.IndexOf(':', PLUGIN_PREFIX.Length);
        return separator > PLUGIN_PREFIX.Length ? server[(separator + 1)..] : server;
    }

    /// <summary>
    /// Looks the vendor's servers up and returns those the role's patterns match, recording both as
    /// <c>worker.tools</c>. A lookup that fails grants nothing: the worker can still work by text
    /// search, and a failed act would cost more than the grant is worth.
    /// </summary>
    /// <param name="vendor">The vendor id, for the log.</param>
    /// <param name="role">The role whose patterns are matched.</param>
    /// <param name="list">Lists the server names the vendor would load for this worker.</param>
    /// <param name="ct">Cancels the lookup on behalf of the caller.</param>
    public static async Task<IReadOnlyList<string>> GrantAsync(string vendor,
                                                               RoleSpec role,
                                                               Func<CancellationToken, Task<IReadOnlyList<string>>> list,
                                                               CancellationToken ct)
    {
        if (role.WorkerTools is not { Count: > 0 } patterns) return [];

        IReadOnlyList<string> servers;
        try
        {
            servers = await list(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            Record("warn", vendor, role, ("error", error.Message));
            return [];
        }

        var granted = Match(patterns, servers);
        Record("info", vendor, role,
               ("servers", granted.Count == 0 ? null : string.Join(", ", granted)),
               ("listed", servers.Count.ToString()));
        return granted;
    }

    private static void Record(string level, string vendor, RoleSpec role, params (string Name, string? Value)[] fields) =>
        RunLog.Current?.Write(level, SOURCE, "worker.tools",
            [("vendor", vendor), ("role", role.Role.ToString()), ("patterns", string.Join(", ", role.WorkerTools ?? [])), .. fields]);
}
