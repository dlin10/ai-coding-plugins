using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static JsonNode? Node(object? value) => value switch
                                                   {
                                                       null => null,
                                                       JsonNode node => node.DeepClone(),
                                                       JsonElement element => JsonNode.Parse(element.GetRawText()),
                                                       string text => JsonValue.Create(text),
                                                       bool boolean => JsonValue.Create(boolean),
                                                       int number => JsonValue.Create(number),
                                                       long number => JsonValue.Create(number),
                                                       double number => JsonValue.Create(number),
                                                       string[] values => new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                                                       _ => throw new InvalidOperationException($"Unsupported JSON value type: {value.GetType().FullName}"),
                                                   };

    public static void Success(string command, object? data)
    {
        var root = new JsonObject
        {
            ["ok"] = true,
            ["command"] = command,
            ["data"] = Node(data) ?? new JsonObject(),
        };
        Console.Out.WriteLine(root.ToJsonString(Options));
    }

    public static void Error(string command, CliFailure failure)
    {
        var error = new JsonObject
        {
            ["code"] = failure.Code,
            ["message"] = failure.Message,
        };
        if (failure.Details is not null)
        {
            error["details"] = failure.Details;
        }

        var root = new JsonObject
        {
            ["ok"] = false,
            ["command"] = command,
            ["error"] = error,
        };
        Console.Out.WriteLine(root.ToJsonString(Options));
    }
}
