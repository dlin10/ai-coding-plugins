using System.CommandLine;

namespace PlanForgeFlow;

internal static class CliCommandTree
{
    private static readonly HashSet<string> BooleanOptions = new(StringComparer.Ordinal)
    {
        "cancel", "retry", "fix", "relock", "amendment", "full", "accept-risk", "delete-owned-artifacts", "purge-generated-agents", "purge-replay-ledger", "verification-passed",
    };

    public static RootCommand Build()
    {
        var root = new RootCommand("Plan Forge Flow grouped CLI");
        foreach (var item in Definitions())
        {
            var parts = item.Key.Split(' ', 2);
            var group = root.Subcommands.FirstOrDefault(command => command.Name == parts[0]);
            if (group is null)
            {
                group = new Command(parts[0]);
                root.Subcommands.Add(group);
            }

            var leaf = new Command(parts[1]);
            foreach (var option in item.Value)
            {
                leaf.Options.Add(BooleanOptions.Contains(option) ? new Option<bool>($"--{option}") : new Option<string?>($"--{option}"));
            }
            group.Subcommands.Add(leaf);
        }

        return root;
    }

    private static IEnumerable<KeyValuePair<string, IReadOnlySet<string>>> Definitions()
    {
        yield return new("plan start", new HashSet<string>(["workspace", "session-context"]));
        yield return new("plan lock", new HashSet<string>(["workspace", "relock", "amendment"]));
        yield return new("approval issue", new HashSet<string>(["workspace"]));
        yield return new("approval resume", new HashSet<string>(["workspace", "purge-replay-ledger"]));
        yield return new("agents install", new HashSet<string>(["workspace"]));
        yield return new("build dispatch", new HashSet<string>(["workspace", "stage", "task-number", "retry", "cancel", "fix", "dispatch-id", "model", "effort", "plan-sha256", "authorization-note", "accept-risk"]));
        yield return new("build complete", new HashSet<string>(["workspace", "task-number", "dispatch-id", "fix", "verification-passed", "authorization-note", "accept-risk"]));
        yield return new("build resolve", new HashSet<string>(["workspace", "conflict", "dispatch-id"]));
        yield return new("build begin", new HashSet<string>(["workspace", "amendment", "relock"]));
        yield return new("review prepare", new HashSet<string>(["workspace", "allow-paths", "full", "plan-sha256", "authorization-note"]));
        yield return new("review authorize-preexisting", new HashSet<string>(["workspace", "authorized-paths", "authorization-note", "accept-risk"]));
        yield return new("review verdict", new HashSet<string>(["workspace", "stage", "verdict", "coverage", "critique-file", "fix", "accept-risk", "authorization-note"]));
        yield return new("session builder", new HashSet<string>(["workspace", "id", "dispatch-id", "model", "effort", "authorization-note"]));
        yield return new("session reviewer", new HashSet<string>(["workspace", "id", "dispatch-id", "model", "effort", "authorization-note"]));
        yield return new("run doctor", new HashSet<string>(["workspace"]));
        yield return new("run status", new HashSet<string>(["workspace"]));
        yield return new("run set", new HashSet<string>(["workspace", "key", "value", "amendment", "accept-risk", "authorization-note"]));
        yield return new("run cleanup", new HashSet<string>(["workspace", "delete-owned-artifacts", "purge-generated-agents", "purge-replay-ledger"]));
    }
}
