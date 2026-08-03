using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PlanForgeFlow;

internal static class JsonOutput
{
    public static void Success<T>(string command, T data)
        => Console.Out.WriteLine(JsonSerialization.Serialize(
            new JsonSuccess<T>(true, command, data),
            TypeInfo<JsonSuccess<T>>()));

    public static void Error(string command, CliFailure failure)
    {
        var details = failure.Details?.ToList();
        var error = new JsonFailure(false, command, new JsonError(failure.Code, failure.Message, details));
        Console.Out.WriteLine(JsonSerialization.Serialize(error, ForgeJsonContext.Default.JsonFailure));
    }

    private static JsonTypeInfo<T> TypeInfo<T>()
        => ForgeJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
           ?? throw new InvalidOperationException($"JSON metadata is not registered for {typeof(T).FullName}");
}
