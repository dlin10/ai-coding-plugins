using System.Text.Json;
using FindFiles.Everything;

namespace FindFiles.Mcp;

/// <summary>
/// The one tool this server exposes. The name carries the differentiator on purpose: a model reads
/// the name first, and "search_files" would be indistinguishable from the Glob and Grep tools it
/// already has. The description states both boundaries, because a tool that only advertises what it
/// can do gets used where it should not be.
/// </summary>
internal static class ToolDefinition
{
    internal const string ToolName = "find_file_anywhere";

    internal const string Description =
        "Instantly find files and folders by name anywhere on this Windows machine using the Everything "
        + "index, with no directory walk. Use it for anything OUTSIDE the current project: other "
        + "repositories, installed programs, %APPDATA%, configuration files, any path you do not know. "
        + "Do NOT use it to search inside the current repository — Glob and ripgrep respect .gitignore "
        + "while this index does not, so it returns hits from bin/, obj/, node_modules/ and sibling "
        + "clones of the same project. It does NOT search file contents; use Grep for that. Matching is "
        + "substring-based, so \"SKILL.md\" also matches \"SKILL.md.diff\".";

    internal static void WriteTool(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("name", ToolName);
        writer.WriteString("description", Description);

        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");

        writer.WritePropertyName("properties");
        writer.WriteStartObject();

        WriteProperty(writer,
                      "query",
                      "string",
                      "Everything search syntax, not a literal string. Wildcards (*.csproj), boolean "
                      + "operators (config !backup), and filters such as ext:jpg;png, size:>1mb and "
                      + "dm:today all work. Include a path fragment with backslashes to pin a folder.");

        WriteProperty(writer,
                      "path",
                      "string",
                      "Restrict the search to this directory tree. Use an absolute path.");

        WriteProperty(writer,
                      "limit",
                      "integer",
                      $"Maximum results to return. Defaults to {SearchRequest.DefaultLimit}.");

        WriteProperty(writer,
                      "recent",
                      "boolean",
                      "Rank by modification date, newest first, instead of by shortest path. Use when "
                      + "looking for something recently changed.");

        writer.WritePropertyName("kind");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WritePropertyName("enum");
        writer.WriteStartArray();
        writer.WriteStringValue("both");
        writer.WriteStringValue("files");
        writer.WriteStringValue("folders");
        writer.WriteEndArray();
        writer.WriteString("description", "Restrict results to files or to folders. Defaults to both.");
        writer.WriteEndObject();

        WriteProperty(writer,
                      "regex",
                      "boolean",
                      "Treat the query as a regular expression instead of Everything syntax.");

        writer.WriteEndObject();

        writer.WritePropertyName("required");
        writer.WriteStartArray();
        writer.WriteStringValue("query");
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteProperty(Utf8JsonWriter writer, string name, string type, string description)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("type", type);
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }
}
