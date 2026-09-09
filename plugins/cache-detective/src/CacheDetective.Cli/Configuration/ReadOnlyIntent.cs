using System.Data.Common;

namespace CacheDetective.Configuration;

/// <summary>
/// Marks a connection as read-only in the connection itself. On a server without availability groups the
/// keyword forbids nothing, but it states the intent where anyone reading the connection — a DBA looking
/// at <c>sys.dm_exec_sessions</c> included — will see it. A value already in the string is left alone:
/// the user's connection string wins over ours.
/// </summary>
internal static class ReadOnlyIntent
{
    private const string KEYWORD = "ApplicationIntent";

    internal static string Apply(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (builder.Keys.Cast<string>().Any(IsIntent))
        {
            return connectionString;
        }

        builder[KEYWORD] = "ReadOnly";
        return builder.ConnectionString;
    }

    /// <summary>Both spellings the keyword has, compared the way a connection string compares keys.</summary>
    private static bool IsIntent(string key) =>
        key.Replace(" ", string.Empty).Equals(KEYWORD, StringComparison.OrdinalIgnoreCase);
}
