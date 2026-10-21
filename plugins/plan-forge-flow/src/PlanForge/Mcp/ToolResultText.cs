using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PlanForge.Mcp;

/// <summary>
/// Sends every JSON tool result compact, with its text as written rather than as <c>\uXXXX</c>.
/// </summary>
/// <remarks>
/// <para>
/// The results are built in a dozen places — the tools, the Job acts, every helper that attaches a
/// warning to a finished result — and each serialised with the default encoder, which escapes all
/// non-ASCII, and most of them indented. The Orchestrator pays for that in context: an escaped
/// Cyrillic letter cost about 4.1 tokens where the letter itself costs 0.5, and on runs held in
/// Russian two thirds of the forge result tokens were escapes and indentation. One filter at the
/// edge decides it for all of them, including results added later; see docs/adr/0042.
/// </para>
/// <para>
/// Only the text block is rewritten: a value is written back as it was read, so nothing the
/// Orchestrator reads changes except its spelling. A text that is not a JSON object or array — the
/// path <c>forge.log.append</c> returns, an error message — passes untouched.
/// </para>
/// </remarks>
internal static class ToolResultText
{
    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Compact { get; } =
        next => async (request, ct) =>
        {
            var result = await next(request, ct);
            foreach (var block in result.Content.OfType<TextContentBlock>())
                block.Text = Compacted(block.Text);
            return result;
        };

    /// <summary>The same JSON without indentation or escaped non-ASCII, or the text itself when it is not a JSON object or array.</summary>
    /// <param name="text">A tool result's text.</param>
    internal static string Compacted(string text)
    {
        if (text.Length == 0 || text[0] is not ('{' or '[')) return text;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return text;
        }

        using (document)
        {
            var buffer = new ArrayBufferWriter<byte>(text.Length);
            using (var writer = new Utf8JsonWriter(buffer, Writer))
                document.RootElement.WriteTo(writer);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }
}
