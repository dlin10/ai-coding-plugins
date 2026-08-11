using System.Text;
using System.Text.Json;
using FindFiles.Everything;
using FindFiles.Formatting;

namespace FindFiles.Mcp;

/// <summary>
/// A minimal MCP server over stdio. Only the four methods this plugin needs are implemented; every
/// other request gets a well-formed "method not found" so a host probing for resources or prompts
/// sees a polite refusal instead of a dead pipe.
/// </summary>
/// <remarks>
/// Nothing except JSON-RPC may ever reach stdout. A single stray write corrupts the stream, and the
/// only symptom the user sees is "the server will not start", so diagnostics go to stderr.
/// </remarks>
internal sealed class McpServer(SearchService searchService)
{
    private const string FallbackProtocolVersion = "2025-06-18";

    private static class JsonRpcError
    {
        internal const int ParseError = -32700;
        internal const int InvalidRequest = -32600;
        internal const int MethodNotFound = -32601;
        internal const int InvalidParams = -32602;
        internal const int InternalError = -32603;
    }

    internal void Run(TextReader input, TextWriter output)
    {
        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = HandleLine(line);
            if (response is not null)
            {
                output.WriteLine(response);
                output.Flush();
            }
        }
    }

    /// <summary>
    /// Handles one JSON-RPC line. Returns the response to write, or <see langword="null"/> for a
    /// notification, which by protocol must not be answered.
    /// </summary>
    internal string? HandleLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            return WriteError(id: null, JsonRpcError.ParseError, exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return WriteError(id: null, JsonRpcError.InvalidRequest, "Request must be a JSON object");
            }

            JsonElement? id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null
                ? idElement.Clone()
                : null;

            if (!root.TryGetProperty("method", out var methodElement)
                || methodElement.ValueKind != JsonValueKind.String)
            {
                return id is null ? null : WriteError(id, JsonRpcError.InvalidRequest, "Missing method");
            }

            var method = methodElement.GetString() ?? string.Empty;
            root.TryGetProperty("params", out var parameters);

            // A request without an id is a notification: act on it, answer nothing.
            if (id is null)
            {
                return null;
            }

            try
            {
                return method switch
                {
                    "initialize" => WriteInitialize(id, parameters),
                    "tools/list" => WriteToolList(id),
                    "tools/call" => WriteToolCall(id, parameters),
                    "ping" => WriteResult(id, static _ => { }),
                    _ => WriteError(id, JsonRpcError.MethodNotFound, $"Unsupported method: {method}"),
                };
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return WriteError(id, JsonRpcError.InternalError, exception.Message);
            }
        }
    }

    private static string WriteInitialize(JsonElement? id, JsonElement parameters)
    {
        // Echo the client's protocol version when it states one: this server's payloads are version
        // independent, and refusing an unfamiliar version would only break hosts needlessly.
        var protocolVersion = FallbackProtocolVersion;
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("protocolVersion", out var requested)
            && requested.ValueKind == JsonValueKind.String
            && requested.GetString() is { Length: > 0 } requestedVersion)
        {
            protocolVersion = requestedVersion;
        }

        return WriteResult(id, writer =>
        {
            writer.WriteString("protocolVersion", protocolVersion);

            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            writer.WritePropertyName("tools");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WritePropertyName("serverInfo");
            writer.WriteStartObject();
            writer.WriteString("name", BuildInfo.ServerName);
            writer.WriteString("version", BuildInfo.Version);
            writer.WriteEndObject();
        });
    }

    private static string WriteToolList(JsonElement? id) =>
        WriteResult(id, writer =>
        {
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            ToolDefinition.WriteTool(writer);
            writer.WriteEndArray();
        });

    private string WriteToolCall(JsonElement? id, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            return WriteError(id, JsonRpcError.InvalidParams, "Missing tool name");
        }

        var toolName = nameElement.GetString();
        if (toolName != ToolDefinition.ToolName)
        {
            return WriteError(id, JsonRpcError.InvalidParams, $"Unknown tool: {toolName}");
        }

        parameters.TryGetProperty("arguments", out var arguments);
        if (!ToolArguments.TryRead(arguments, out var request, out var argumentError))
        {
            return WriteError(id, JsonRpcError.InvalidParams, argumentError);
        }

        var outcome = searchService.Execute(request);
        var text = OutputText.Render(outcome, request);

        return WriteResult(id, writer =>
        {
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();

            // Everything being down or the name being absent are answers, not protocol failures:
            // the text explains what to do next, and the model should read it rather than retry.
            writer.WriteBoolean("isError", outcome.Status is SearchStatus.Failed);
        });
    }

    private static string WriteResult(JsonElement? id, Action<Utf8JsonWriter> writeBody) =>
        WriteEnvelope(id, writer =>
        {
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        });

    private static string WriteError(JsonElement? id, int code, string message) =>
        WriteEnvelope(id, writer =>
        {
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });

    private static string WriteEnvelope(JsonElement? id, Action<Utf8JsonWriter> writeBody)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");

            writer.WritePropertyName("id");
            if (id is { } value)
            {
                value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writeBody(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
