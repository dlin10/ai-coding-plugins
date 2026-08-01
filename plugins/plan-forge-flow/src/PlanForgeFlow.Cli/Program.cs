using System.Text.Json.Serialization;

namespace PlanForgeFlow;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        if (args.Length >= 2 && string.Equals(args[0], "hook", StringComparison.Ordinal) &&
            string.Equals(args[1], "capture-context", StringComparison.Ordinal))
        {
            return Task.FromResult(HookService.Run(Console.In.ReadToEnd()));
        }

        return new CliApplication().RunAsync(args);
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionCapture))]
internal partial class ForgeJsonContext : JsonSerializerContext
{
}
