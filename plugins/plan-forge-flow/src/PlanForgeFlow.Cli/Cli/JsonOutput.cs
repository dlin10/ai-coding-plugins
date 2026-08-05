using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PlanForgeFlow.Serialization;

namespace PlanForgeFlow.Cli;

internal static class JsonOutput
{
    public static void Success<T>(string command, T data, JsonTypeInfo<JsonSuccess<T>> typeInfo)
        => Console.Out.WriteLine(JsonSerializer.Serialize(
            new JsonSuccess<T>(true, command, data),
            typeInfo));

    public static void Error(string command, CliFailure failure)
    {
        var details = failure.Details?.ToList();
        var error = new JsonFailure(false, command, new JsonError(failure.Code, failure.Message, details));
        Console.Out.WriteLine(JsonSerializer.Serialize(error, ForgeJsonContext.Default.JsonFailure));
    }
}
