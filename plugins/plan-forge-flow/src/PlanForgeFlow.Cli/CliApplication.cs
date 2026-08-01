using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed partial class CliApplication
{
    public int Run(string[] args)
    {
        var command = ResolveCommand(args, out var optionOffset);
        try
        {
            if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
            {
                PrintHelp(command);
                return 0;
            }

            if (!CliCommands.TryGet(command, out var definition))
            {
                throw new CliFailure("usage", $"unknown command '{command}'");
            }

            var parsed = ParsedArgs.Parse(args.Skip(optionOffset));
            ValidateOptions(definition, parsed);
            var context = CommandContext.Create(definition, parsed);
            switch (command)
            {
                case "plan start":
                    JsonOutput.Success(command, new JsonObject { ["mode"] = "plan" });
                    break;
                case "plan lock":
                    JsonOutput.Success(command, LockPlan(context));
                    break;
                case "plan materialize":
                    JsonOutput.Success(command, MaterializePlan(context));
                    break;
                case "agents install":
                    JsonOutput.Success(command, InstallAgents());
                    break;
                case "build dispatch":
                    JsonOutput.Success(command, Dispatch(context));
                    break;
                case "build complete":
                    JsonOutput.Success(command, Complete(context));
                    break;
                case "build resolve":
                    JsonOutput.Success(command, ResolveBuild(context));
                    break;
                case "build begin":
                    JsonOutput.Success(command, BeginBuild(context));
                    break;
                case "review prepare":
                    JsonOutput.Success(command, PrepareReview(context));
                    break;
                case "review authorize-preexisting":
                    JsonOutput.Success(command, AuthorizePreexisting(context));
                    break;
                case "review verdict":
                    JsonOutput.Success(command, Verdict(context));
                    break;
                case "session builder":
                case "session reviewer":
                    JsonOutput.Success(command, Session(context, command.EndsWith("builder", StringComparison.Ordinal) ? ForgeRole.Builder : ForgeRole.Reviewer));
                    break;
                case "run doctor":
                    JsonOutput.Success(command, Doctor(context.Workspace));
                    break;
                case "run status":
                    if (!File.Exists(StateStore.StatePath(context.Workspace)))
                    {
                        JsonOutput.Success(command, new JsonObject { ["exists"] = false });
                    }
                    else
                    {
                        var status = StateStore.Load(context.Workspace).ToJson();
                        status["exists"] = true;
                        JsonOutput.Success(command, status);
                    }
                    break;
                case "run set":
                    JsonOutput.Success(command, Set(context));
                    break;
                case "run cleanup":
                    Materializer.Cleanup(context.Workspace, context.Args.Has("purge-generated-agents"));
                    JsonOutput.Success(command, new JsonObject { ["cleaned"] = true, ["purgedAgents"] = context.Args.Has("purge-generated-agents") });
                    break;
                default:
                    throw new CliFailure("usage", $"unknown command '{command}'");
            }
            return 0;
        }
        catch (CliFailure failure)
        {
            JsonOutput.Error(command, failure);
            return failure.ExitCode;
        }
        catch (Exception error)
        {
            JsonOutput.Error(command, new CliFailure("unexpected", error.Message));
            return 1;
        }
    }

    private static string ResolveCommand(string[] args, out int optionOffset)
    {
        optionOffset = 0;
        if (args.Length == 0) return "help";
        if (args[0].StartsWith("--", StringComparison.Ordinal)) return "help";
        if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            optionOffset = 2;
            return $"{args[0]} {args[1]}";
        }

        optionOffset = 1;
        return args[0];
    }

    private static void ValidateOptions(CliCommandDefinition command, ParsedArgs parsed)
    {
        if (parsed.Positionals.Count > 0)
        {
            throw new CliFailure("usage", $"{command.Name} does not accept positional arguments");
        }

        foreach (var name in parsed.Names)
        {
            if (!command.Options.Contains(name)) throw new CliFailure("usage", $"unknown option --{name}");
            if (!CliCommands.IsBoolean(name) && string.Equals(parsed.Get(name), "true", StringComparison.Ordinal)) throw new CliFailure("usage", $"--{name} requires a value");
        }
    }

    private static void PrintHelp(string command)
    {
        JsonOutput.Success(command, new JsonObject
        {
            ["usage"] = $"planforge {command} [options]",
            ["commands"] = new JsonArray(
                                         CliCommands.Names.Select(name => (JsonNode)name).Append("hook capture-context").ToArray()),
        });
    }

}
