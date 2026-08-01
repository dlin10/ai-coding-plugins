using System.CommandLine;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed partial class CliApplication
{
    private static readonly RootCommand CommandTree = CliCommandTree.Build();
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedOptions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["plan start"] = Set("workspace", "session-context"),
            ["plan lock"] = Set("workspace", "relock", "amendment"),
            ["approval issue"] = Set("workspace"),
            ["approval resume"] = Set("workspace"),
            ["agents install"] = Set("workspace"),
            ["build dispatch"] = Set("workspace", "stage", "task-number", "retry", "cancel", "dispatch-id", "model", "effort", "plan-sha256", "authorization-note", "accept-risk"),
            ["build complete"] = Set("workspace", "task-number", "dispatch-id", "verification-passed", "authorization-note", "accept-risk"),
            ["build resolve"] = Set("workspace", "conflict", "dispatch-id"),
            ["build begin"] = Set("workspace", "amendment", "relock"),
            ["review prepare"] = Set("workspace", "allow-paths", "full", "plan-sha256", "authorization-note"),
            ["review authorize-preexisting"] = Set("workspace", "authorized-paths", "authorization-note", "accept-risk"),
            ["review verdict"] = Set("workspace", "stage", "critique-file", "accept-risk", "authorization-note"),
            ["session builder"] = Set("workspace", "id", "dispatch-id", "model", "effort", "authorization-note"),
            ["session reviewer"] = Set("workspace", "id", "dispatch-id", "model", "effort", "authorization-note"),
            ["run doctor"] = Set("workspace"),
            ["run status"] = Set("workspace"),
            ["run set"] = Set("workspace", "key", "value", "amendment", "accept-risk", "authorization-note"),
            ["run cleanup"] = Set("workspace", "delete-owned-artifacts", "purge-generated-agents"),
        };

    public async Task<int> RunAsync(string[] args)
    {
        var command = ResolveCommand(args, out var optionOffset);
        try
        {
            if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
            {
                PrintHelp(command);
                return 0;
            }

            if (!AllowedOptions.ContainsKey(command))
            {
                throw new CliFailure("usage", $"unknown command '{command}'");
            }

            var typedParse = CommandTree.Parse(args);
            if (typedParse.Errors.Count > 0) throw new CliFailure("usage", typedParse.Errors[0].Message);
            var parsed = ParsedArgs.Parse(args.Skip(optionOffset));
            ValidateOptions(command, parsed);
            var workspace = RepositoryPaths.CanonicalWorkspaceRoot(parsed.Get("workspace") ?? Directory.GetCurrentDirectory());
            switch (command)
            {
                case "plan start":
                    RequireMode(workspace, "plan", parsed.Get("session-context"));
                    JsonOutput.Success(command, new JsonObject { ["mode"] = "plan" });
                    break;
                case "plan lock":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, LockPlan(workspace, parsed));
                    break;
                case "approval issue":
                    RequireMode(workspace, "plan");
                    JsonOutput.Success(command, IssueApproval(parsed, workspace));
                    break;
                case "approval resume":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Materializer.Materialize(RepositoryPaths.Identify(workspace), ResolveApprovalWrapper(workspace)));
                    break;
                case "agents install":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, InstallAgents());
                    break;
                case "build dispatch":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Dispatch(workspace, parsed));
                    break;
                case "build complete":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Complete(workspace, parsed));
                    break;
                case "build resolve":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, ResolveBuild(workspace, parsed));
                    break;
                case "build begin":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, BeginBuild(workspace, parsed));
                    break;
                case "review prepare":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, PrepareReview(workspace, parsed));
                    break;
                case "review authorize-preexisting":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, AuthorizePreexisting(workspace, parsed));
                    break;
                case "review verdict":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Verdict(workspace, parsed));
                    break;
                case "session builder":
                case "session reviewer":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Session(workspace, parsed, command.EndsWith("builder", StringComparison.Ordinal) ? "builder" : "reviewer"));
                    break;
                case "run doctor":
                    JsonOutput.Success(command, Doctor(workspace));
                    break;
                case "run status":
                    if (!File.Exists(StateStore.StatePath(workspace)))
                    {
                        JsonOutput.Success(command, new JsonObject { ["exists"] = false });
                    }
                    else
                    {
                        var status = StateStore.Load(workspace);
                        status["exists"] = true;
                        JsonOutput.Success(command, status);
                    }
                    break;
                case "run set":
                    RequireMode(workspace, "default");
                    JsonOutput.Success(command, Set(workspace, parsed));
                    break;
                case "run cleanup":
                    RequireMode(workspace, "default");
                    Materializer.Cleanup(workspace, parsed.Has("delete-owned-artifacts"), parsed.Has("purge-generated-agents"));
                    JsonOutput.Success(command, new JsonObject { ["cleaned"] = true, ["deletedArtifacts"] = parsed.Has("delete-owned-artifacts"), ["purgedAgents"] = parsed.Has("purge-generated-agents") });
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

    private static void ValidateOptions(string command, ParsedArgs parsed)
    {
        if (parsed.Positionals.Count > 0)
        {
            throw new CliFailure("usage", $"{command} does not accept positional arguments");
        }

        foreach (var name in parsed.Names)
        {
            if (!AllowedOptions[command].Contains(name)) throw new CliFailure("usage", $"unknown option --{name}");
            if (!BooleanOptionNames.Contains(name) && string.Equals(parsed.Get(name), "true", StringComparison.Ordinal)) throw new CliFailure("usage", $"--{name} requires a value");
        }
    }

    private static readonly IReadOnlySet<string> BooleanOptionNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "cancel", "retry", "relock", "amendment", "full", "accept-risk", "delete-owned-artifacts", "purge-generated-agents", "verification-passed",
    };

    private static IReadOnlySet<string> Set(params string[] values) => values.ToHashSet(StringComparer.Ordinal);

    private static void PrintHelp(string command)
    {
        JsonOutput.Success(command, new JsonObject
        {
            ["usage"] = $"planforge {command} [options]",
            ["commands"] = new JsonArray(
                                         "plan start", "plan lock", "approval issue", "approval resume",
                                         "agents install", "build dispatch", "build complete", "build resolve", "build begin",
                                         "review prepare", "review authorize-preexisting", "review verdict",
                                         "session builder", "session reviewer", "run doctor", "run status", "run set", "run cleanup",
                                         "hook capture-context"),
        });
    }

    private static void RequireMode(string workspace, string expected, string? explicitCapturePath = null)
    {
        var capture = SessionCapture.Read(workspace, explicitCapturePath);
        if (capture?.CollaborationMode != expected) throw new CliFailure("environment", $"command requires a fresh {expected}-mode capture");
    }





}
