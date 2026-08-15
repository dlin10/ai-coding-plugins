using System.Text;
using System.Text.Json;

namespace PlanForge.Vendors;

/// <summary>
/// Structure for vendors with no native schema support: the schema goes into the prompt, validation
/// happens here, and a rejected reply is retried once carrying the reason. Two of the three vendors
/// are in that position — Cursor has no schema flag, and the Codex App Server has no schema field.
/// </summary>
internal static class SchemaInPrompt
{
    public const int MaxAttempts = 2;

    public static string Compose(string prompt, string schemaJson, string? previousFailure)
    {
        var composed = new StringBuilder()
            .AppendLine(prompt)
            .AppendLine()
            .AppendLine("Reply with a single JSON object and nothing else — no prose, no code fence.")
            .AppendLine("It must validate against this schema:")
            .AppendLine()
            .AppendLine(schemaJson);

        if (previousFailure is not null)
            composed.AppendLine()
                    .Append("Your previous reply was rejected: ").AppendLine(previousFailure)
                    .AppendLine("Return only the JSON object this time.");

        return composed.ToString();
    }

    public static bool TryExtract<T>(string text, VendorSchema<T> schema, out T value, out string? failure)
    {
        value = default!;

        // The reply carries prose around the object — routing banners, explanations, code fences.
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            failure = "the reply contained no JSON object";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(text[start..(end + 1)], schema.TypeInfo);
            if (parsed is null)
            {
                failure = "the JSON object was null";
                return false;
            }

            value = parsed;
            failure = null;
            return true;
        }
        catch (JsonException error)
        {
            failure = error.Message;
            return false;
        }
    }
}
