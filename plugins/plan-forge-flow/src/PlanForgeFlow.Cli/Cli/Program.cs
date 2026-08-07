using PlanForgeFlow.Codex;
using PlanForgeFlow.Serialization;

namespace PlanForgeFlow.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && string.Equals(args[0], "hook", StringComparison.Ordinal) &&
            string.Equals(args[1], "capture-context", StringComparison.Ordinal))
        {
            return HookService.Run(JsonInput.Read(Console.In));
        }

        return new CliApplication().Run(args);
    }
}
