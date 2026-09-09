using System.Reflection;
using CacheDetective.Configuration;
using CacheDetective.Graph;
using CacheDetective.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>What a caller outside this repository already depends on: the names it calls, the schema
/// versions it reads and the kinds it may resolve. Each assertion names its members rather than counting
/// them, so a tool renamed, a version bumped in silence or a kind dropped fails as the thing it is.
/// </summary>
public sealed class PublicContractTests
{
    [Fact]
    public void Server_exposes_exactly_these_tool_names()
    {
        var names = typeof(WorkspaceSession).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance |
                                                BindingFlags.DeclaredOnly))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["annotate", "export_graph", "find_issues", "find_unguarded_writes", "get_evidence",
                      "get_unresolved", "index_database", "index_solution", "trace_key", "trace_table",
                      "verify_finding", "workspace_init", "workspace_status"], names);
    }

    /// <summary>Read off an export rather than off the constant: what a reader of the file sees is the
    /// number in the file, not the number in the source.</summary>
    [Fact]
    public void ExportGraph_emits_schema_version_three()
    {
        var export = TraceQueries.ExportGraph(new CacheGraph(), null, null);

        Assert.Equal(3, export.Version);
    }

    [Fact]
    public void Workspace_configuration_version_is_one()
    {
        Assert.Equal(1, WorkspaceConfiguration.CurrentVersion);
        Assert.Equal(1, new WorkspaceConfiguration().Version);
    }

    [Fact]
    public void Annotate_resolves_exactly_these_kinds()
    {
        var kinds = Enum.GetValues<UnresolvedKind>()
            .Select(FindingQueries.KindName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["cache_api", "call", "event", "event_api", "key", "role", "sql"], kinds);
    }
}
