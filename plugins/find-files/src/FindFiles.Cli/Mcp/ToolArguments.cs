using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using FindFiles.Everything;

namespace FindFiles.Mcp;

/// <summary>Maps the JSON arguments of a <c>tools/call</c> onto a <see cref="SearchRequest"/>.</summary>
internal static class ToolArguments
{
    /// <summary>Guards against a caller asking for a result set large enough to swamp its own context.</summary>
    internal const int MaxLimit = 500;

    internal static bool TryRead(JsonElement arguments,
                                [NotNullWhen(true)] out SearchRequest? request,
                                [NotNullWhen(false)] out string? error)
    {
        request = null;
        error = null;

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "Expected an arguments object";
            return false;
        }

        if (!TryReadString(arguments, "query", out var query) || string.IsNullOrWhiteSpace(query))
        {
            error = "Argument \"query\" is required and must be a non-empty string";
            return false;
        }

        TryReadString(arguments, "path", out var path);

        var limit = SearchRequest.DefaultLimit;
        if (arguments.TryGetProperty("limit", out var limitElement) && limitElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadInt(limitElement, out limit) || limit <= 0)
            {
                error = "Argument \"limit\" must be a positive integer";
                return false;
            }

            limit = Math.Min(limit, MaxLimit);
        }

        var kind = EntryKind.Both;
        if (TryReadString(arguments, "kind", out var kindText) && !string.IsNullOrWhiteSpace(kindText))
        {
            switch (kindText.ToLowerInvariant())
            {
                case "both":
                    kind = EntryKind.Both;
                    break;
                case "files":
                    kind = EntryKind.Files;
                    break;
                case "folders":
                    kind = EntryKind.Folders;
                    break;
                default:
                    error = "Argument \"kind\" must be one of: both, files, folders";
                    return false;
            }
        }

        request = new SearchRequest
        {
            Query = query,
            Path = string.IsNullOrWhiteSpace(path) ? null : path,
            Limit = limit,
            Recent = ReadBoolean(arguments, "recent"),
            Kind = kind,
            Regex = ReadBoolean(arguments, "regex"),
        };

        return true;
    }

    private static bool TryReadString(JsonElement arguments, string name, out string? value)
    {
        value = null;
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null;
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt32(out value);
            case JsonValueKind.String:
                // Some clients stringify numeric arguments; accepting that costs nothing.
                return int.TryParse(element.GetString(),
                                    NumberStyles.Integer,
                                    CultureInfo.InvariantCulture,
                                    out value);
            default:
                value = 0;
                return false;
        }
    }

    private static bool ReadBoolean(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;
}
