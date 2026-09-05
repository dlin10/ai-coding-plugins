using PlanForge.Run;

namespace PlanForge.Mcp;

/// <summary>
/// What <c>forge.begin</c> was told about the run's gates, checked before anything is written. Both
/// are per-run facts: the environment a gate command needs and the paths a builder may write to
/// outside the workspace do not change between tasks, so they are asked for once.
/// </summary>
internal sealed record GateSettings(IReadOnlyDictionary<string, string>? Environment, IReadOnlyList<string>? BuilderRoots)
{
    public static GateSettings Validate(IReadOnlyDictionary<string, string>? environment, IReadOnlyList<string>? builderRoots)
    {
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Contains('=') || name.Any(char.IsWhiteSpace))
                    throw new ArgumentRejectedException($"gateEnvironment has a name that is not a variable name: '{name}'");
                if (value is null)
                    throw new ArgumentRejectedException($"gateEnvironment.{name} has no value");
            }
        }

        if (builderRoots is not null)
        {
            foreach (var root in builderRoots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
                    throw new ArgumentRejectedException($"builderRoots must be absolute paths, and '{root}' is not");
            }
        }

        return new GateSettings(environment is { Count: > 0 } ? environment : null,
                                builderRoots is { Count: > 0 } ? builderRoots : null);
    }
}
