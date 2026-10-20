using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlanForge.Diagnostics;

namespace PlanForge.Mcp;

/// <summary>
/// Puts the reason a tool failed in front of the orchestrator instead of only in the run log.
/// </summary>
/// <remarks>
/// <para>
/// The SDK replaces the message of any exception that is not an <c>McpException</c> with a generic
/// one, deliberately, so that a server does not leak whatever a stray exception happens to carry.
/// The cost was that a run died with "An error occurred invoking 'forge.plan.review'." and the
/// reason — a prompt file that was never deployed — reached nobody who could act on it. Every
/// exception declared in this assembly is written for exactly that reader, so those pass through
/// with their message; everything else keeps the SDK's generic answer.
/// </para>
/// <para>
/// A filter rather than a wrapper inside the tools, because <see cref="Run.RunDirectory.Open"/>
/// runs before the run's log exists and so before anything a tool could wrap: a bad
/// <c>workspaceRoot</c> or a lost <c>runId</c> fails earlier than every other failure and would
/// otherwise be the one class of error left mute.
/// </para>
/// <para>
/// Earlier still, the SDK binds the arguments, and a call it cannot bind fails with exceptions of
/// its own that it then blanks: a <c>forge.plan.confirm</c> without <c>plan</c> came back "An error
/// occurred", and only <c>forge.log</c> held the reason. Neither exception is enough to answer from:
/// a missing argument is an <see cref="ArgumentException"/> naming the first one only, and a value
/// of the wrong type a <see cref="JsonException"/> naming the .NET type but not the argument. So
/// the answer comes from the schema the tool publishes — the contract the orchestrator was shown —
/// and names every required argument absent or null, or every value of the wrong JSON type at any
/// depth. It explains a call that has already failed and never refuses one in advance, because the
/// schema is stricter than the binder about nulls: <c>forge.plan.patch</c> publishes its edits as
/// objects, takes a null among them, and refuses it itself with the edit's index and the sizes it
/// measures.
/// </para>
/// </remarks>
internal static class ToolErrors
{
    private static readonly Assembly Own = typeof(ToolErrors).Assembly;

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Surfaced { get; } =
        next => async (request, ct) =>
        {
            try
            {
                return await next(request, ct);
            }
            // Cancellation is the host taking the call away rather than an answer to give it, so it
            // flows on untouched. Belonging to no assembly of ours used to be enough to arrange
            // that; it stopped being enough when TurnCutShortException — ours, and a cancellation —
            // was added, so the exclusion is now stated rather than inferred.
            catch (Exception error) when (error is not OperationCanceledException && IsOwn(error))
            {
                return ErrorResult(error.Message);
            }
            // What the SDK's binding throws. A schema that admits the arguments leaves the failure to
            // the tool, and the SDK's generic answer stands.
            catch (Exception error) when (error is ArgumentException or JsonException
                                          && request.MatchedPrimitive is McpServerTool tool
                                          && Unbindable(tool.ProtocolTool, request.Params?.Arguments) is { } reason)
            {
                // The SDK logged its own exception as it escaped; this one no longer escapes.
                RunLog.Current?.Write("error", "server", "tool.unbindable",
                    ("tool", tool.ProtocolTool.Name), ("error", reason), ("exception", error.ToString()));
                return ErrorResult(reason);
            }
        };

    internal static bool IsOwn(Exception error) => error.GetType().Assembly == Own;

    /// <summary>
    /// Says where a call's arguments break the tool's published schema, or null when they do not.
    /// </summary>
    /// <param name="tool">The tool as published, whose input schema is the contract.</param>
    /// <param name="arguments">The arguments the call sent, absent when it sent none.</param>
    /// <returns>Every missing required argument; failing those, every value of the wrong type.</returns>
    private static string? Unbindable(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        var schema = tool.InputSchema;
        var missing = schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(name => name.GetString()!).Where(name => !Sent(arguments, name)).ToList()
            : [];
        if (missing.Count > 0) return $"{tool.Name} requires {string.Join(", ", missing)}";

        if (arguments is null || !schema.TryGetProperty("properties", out var properties)) return null;
        var wrong = arguments.SelectMany(argument => properties.TryGetProperty(argument.Key, out var declared)
                                                         ? Mismatches(declared, argument.Value, argument.Key)
                                                         : [])
                             .ToList();
        return wrong.Count > 0 ? $"{tool.Name} requires {string.Join("; ", wrong)}" : null;
    }

    private static bool Sent(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind != JsonValueKind.Null;

    /// <summary>
    /// Walks one value beside the schema that declares it, naming each place whose JSON type the
    /// schema does not admit.
    /// </summary>
    /// <param name="schema">The schema of <paramref name="value"/>.</param>
    /// <param name="value">The value the call sent.</param>
    /// <param name="path">Where <paramref name="value"/> sits in the arguments, for the message.</param>
    private static IEnumerable<string> Mismatches(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("type", out var type) && !Admits(type, value.ValueKind))
        {
            yield return $"{path} to be {string.Join(" or ", Names(type))}, not {Kind(value.ValueKind)}";
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;
            var others = schema.TryGetProperty("additionalProperties", out var rest) ? rest : default;
            foreach (var property in value.EnumerateObject())
            {
                var inner = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var named)
                    ? named
                    : others;
                if (inner.ValueKind != JsonValueKind.Object) continue;
                foreach (var found in Mismatches(inner, property.Value, $"{path}.{property.Name}"))
                    yield return found;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                foreach (var found in Mismatches(items, item, $"{path}[{index++}]"))
                    yield return found;
        }
    }

    private static bool Admits(JsonElement type, JsonValueKind kind)
    {
        var names = Names(type);
        return names.Contains(Kind(kind)) || kind == JsonValueKind.Number && names.Contains("integer");
    }

    private static string[] Names(JsonElement type) =>
        type.ValueKind == JsonValueKind.Array ? [.. type.EnumerateArray().Select(name => name.GetString()!)] : [type.GetString()!];

    private static string Kind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => "number",
        JsonValueKind.String => "string",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => "null"
    };

    private static CallToolResult ErrorResult(string text) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = text }]
    };
}
