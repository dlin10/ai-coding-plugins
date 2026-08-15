using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Review;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;
using PlanForgeFlow.Cursor;
using PlanForgeFlow.Pending;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.OpenAI;
using PlanForgeFlow.Claude;
using PlanForgeFlow.Codex;
using ForgeWorkflow = PlanForgeFlow.Workflow.Planning.ForgeWorkflow;

namespace PlanForgeFlow.Cli;

internal sealed class CliApplication
{
    private sealed record HostPendingHandlers(Func<CommandContext, TextReader, PendingRun> Stage,
                                              Func<CommandContext, PendingRun> Finalize,
                                              Func<CommandContext, PendingRun> Abandon,
                                              Func<CommandContext, PendingRun> Invalidate,
                                              Func<CommandContext, TextReader, PendingRun> RecordResponse);

    private static readonly IReadOnlyDictionary<HostKind, HostPendingHandlers> PendingHandlers = new Dictionary<HostKind, HostPendingHandlers>
    {
        [HostKind.Cursor] = new(CursorCommandHandlers.Stage,
                                CursorCommandHandlers.Finalize,
                                CursorCommandHandlers.Abandon,
                                CursorCommandHandlers.Invalidate,
                                CursorCommandHandlers.RecordResponse),
        [HostKind.Claude] = new(ClaudeCommandHandlers.Stage,
                                ClaudeCommandHandlers.Finalize,
                                ClaudeCommandHandlers.Abandon,
                                ClaudeCommandHandlers.Invalidate,
                                ClaudeCommandHandlers.RecordResponse),
    };

    public int Run(string[] args)
    {
        if (args.Length >= 2 && string.Equals(args[0], "hook", StringComparison.Ordinal) &&
            string.Equals(args[1], "capture-context", StringComparison.Ordinal))
        {
            return HookService.Run(JsonInput.Read(Console.In));
        }

        if (args.Length >= 2 && string.Equals(args[0], "hook", StringComparison.Ordinal) &&
            string.Equals(args[1], "claude-workflow", StringComparison.Ordinal))
        {
            return ClaudeWorkflowHooks.Run(JsonInput.Read(Console.In));
        }

#if DEBUG
        if (Environment.GetEnvironmentVariable("FORGE_TEST_HOLD_LOCK") is { Length: > 0 } marker)
        {
            var workspaceArgument = args.SkipWhile(value => value != "--workspace").Skip(1).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(workspaceArgument))
            {
                var workspace = RepositoryPaths.CanonicalWorkspaceRoot(workspaceArgument);
                using var repositoryLock = RepositoryRunLock.Acquire(RepositoryPaths.Identify(workspace), HostKind.Codex);
                using var stateLock = ForgeStateLock.Acquire(workspace);
                File.WriteAllText(marker, "held");
                Thread.Sleep(TimeSpan.FromSeconds(30));
            }
        }
#endif
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
            ClaudeCommandHandlers.RequireExecutionForCommand(context);
            switch (command)
            {
                case "plan lock":
                    JsonOutput.Success(command, ForgeWorkflow.LockPlan(context), ForgeJsonContext.Default.JsonSuccessForgeState);
                    break;
                case "plan materialize":
                    JsonOutput.Success(command,
                                       context.Host switch
                                       {
                                           HostKind.Cursor => ForgeWorkflow.MaterializeCursorPlan(context),
                                           HostKind.Claude => ClaudeCommandHandlers.Materialize(context),
                                           _ => ForgeWorkflow.MaterializePlan(context),
                                       },
                                       ForgeJsonContext.Default.JsonSuccessMaterializeData);
                    break;
                case "plan stage":
                    JsonOutput.Success(command, PendingHandler(context.Host).Stage(context, Console.In), ForgeJsonContext.Default.JsonSuccessPendingRun); break;
                case "plan finalize":
                    JsonOutput.Success(command, PendingHandler(context.Host).Finalize(context), ForgeJsonContext.Default.JsonSuccessPendingRun); break;
                case "plan abandon":
                    JsonOutput.Success(command, PendingHandler(context.Host).Abandon(context), ForgeJsonContext.Default.JsonSuccessPendingRun); break;
                case "plan invalidate":
                    JsonOutput.Success(command, PendingHandler(context.Host).Invalidate(context), ForgeJsonContext.Default.JsonSuccessPendingRun); break;
                case "agents install":
                    JsonOutput.Success(command, Workflow.ForgeWorkflow.InstallAgents(), ForgeJsonContext.Default.JsonSuccessInstallAgentsData);
                    break;
                case "build dispatch":
                    JsonOutput.Success(command, Workflow.ForgeWorkflow.Dispatch(context), ForgeJsonContext.Default.JsonSuccessDispatchState);
                    break;
                case "build complete":
                    JsonOutput.Success(command, Workflow.ForgeWorkflow.Complete(context), ForgeJsonContext.Default.JsonSuccessDispatchState);
                    break;
                case "build resolve":
                    JsonOutput.Success(command, Workflow.ForgeWorkflow.ResolveBuild(context), ForgeJsonContext.Default.JsonSuccessForgeState);
                    break;
                case "build begin":
                    JsonOutput.Success(command, Workflow.ForgeWorkflow.BeginBuild(context), ForgeJsonContext.Default.JsonSuccessForgeState);
                    break;
                case "review prepare":
                    JsonOutput.Success(command, ForgeReview.Prepare(context), ForgeJsonContext.Default.JsonSuccessReviewManifest);
                    break;
                case "review authorize-preexisting":
                    JsonOutput.Success(command, ForgeReview.AuthorizePreexisting(context), ForgeJsonContext.Default.JsonSuccessAuthorizationData);
                    break;
                case "review verdict":
                    JsonOutput.Success(command, ForgeReview.Verdict(context), ForgeJsonContext.Default.JsonSuccessVerdictData);
                    break;
                case "review record-response":
                    JsonOutput.Success(command,
                                       PendingHandler(context.Host).RecordResponse(context, Console.In),
                                       ForgeJsonContext.Default.JsonSuccessPendingRun); break;
                case "session builder":
                case "session reviewer":
                    JsonOutput.Success(command,
                                       Workflow.ForgeWorkflow.RegisterSession(context,
                                                                              command.EndsWith("builder", StringComparison.Ordinal)
                                                                                  ? ForgeRole.Builder
                                                                                  : ForgeRole.Reviewer),
                                       ForgeJsonContext.Default.JsonSuccessAgentState);
                    break;
                case "session start":
                    JsonOutput.Success(command,
                                       AppServerSessions.Start(context.Workspace,
                                                               context.Args.GetRequired("role"),
                                                               context.Args.GetRequired("model"),
                                                               context.Args.GetRequired("effort"),
                                                               context.Args.Get("thread-id"),
                                                               Console.In.ReadToEnd(),
                                                               context.Host),
                                       ForgeJsonContext.Default.JsonSuccessAppServerSessionState);
                    break;
                case "session status":
                    JsonOutput.Success(command,
                                       AppServerSessions.Status(context.Workspace, context.Args.GetRequired("session-id"), context.Host),
                                       ForgeJsonContext.Default.JsonSuccessAppServerSessionState);
                    break;
                case "session result":
                    JsonOutput.Success(command,
                                       AppServerSessions.Result(context.Workspace, context.Args.GetRequired("session-id"), context.Host),
                                       ForgeJsonContext.Default.JsonSuccessAppServerSessionResult);
                    break;
                case "session cancel":
                    JsonOutput.Success(command,
                                       AppServerSessions.Cancel(context.Workspace, context.Args.GetRequired("session-id"), context.Host),
                                       ForgeJsonContext.Default.JsonSuccessAppServerSessionState);
                    break;
                case "session worker":
                    AppServerSessions.RunWorker(context.Workspace, context.Args.GetRequired("session-id"), context.Host);
                    JsonOutput.Success(command, new WorkerData(true), ForgeJsonContext.Default.JsonSuccessWorkerData);
                    break;
                case "run doctor":
                    JsonOutput.Success(command, Workflow.ForgeWorkflow.Doctor(context.Workspace, context.Host), ForgeJsonContext.Default.JsonSuccessDoctorData);
                    break;
                case "run begin":
                    JsonOutput.Success(command, ClaudeCommandHandlers.Begin(context), ForgeJsonContext.Default.JsonSuccessClaudeActivation);
                    break;
                case "run abandon":
                    JsonOutput.Success(command, ClaudeCommandHandlers.AbandonRun(context), ForgeJsonContext.Default.JsonSuccessClaudeActivation);
                    break;
                case "run status":
                    RepositoryIdentity? statusRepository = null;
                    StatusPresentData? statusState = null;
                    if (File.Exists(StateStore.StatePath(context.Workspace)))
                    {
                        var state = StateStore.Load(context.Workspace);
                        if (state.Host != context.Host) throw new CliFailure("state", "Forge state host does not match --host", 3);
                        statusRepository = RepositoryPaths.Identify(context.Workspace);
                        if (state.RepositoryScopeId != RepositoryPaths.ScopeId(statusRepository))
                            throw new CliFailure("state", "Forge state repository scope does not match this workspace", 3);
                        statusState = new StatusPresentData(ForgeState.Version,
                                                            ForgeState.Generation,
                                                            state.CreatedAt,
                                                            state.UpdatedAt,
                                                            state.Workflow,
                                                            state.Models,
                                                            state.Agents,
                                                            state.Dispatch,
                                                            state.Baselines,
                                                            state.Review,
                                                            true,
                                                            ForgeState.SchemaVersion,
                                                            state.Host,
                                                            state.SourceRun,
                                                            state.ModelWaiverAudit,
                                                            state.ReviewerGuarantee,
                                                            state.ApprovalGuarantee,
                                                            state.RepositoryScopeId);
                    }

                    var pendingRun = context.Host is HostKind.Cursor or HostKind.Claude
                                         ? PendingRuns.Status(statusRepository ?? RepositoryPaths.Identify(context.Workspace), context.Host)
                                         : null;
                    var statusIdentity = statusRepository ?? RepositoryPaths.Identify(context.Workspace);
                    var activation = context.Host == HostKind.Claude
                                         ? ClaudeActivations.Status(statusIdentity, ClaudeActivations.TryCurrentSessionId())
                                         : null;
                    JsonOutput.Success(command, new RunStatusData(statusState, pendingRun, activation), ForgeJsonContext.Default.JsonSuccessRunStatusData);
                    break;
                case "run set":
                    JsonOutput.Success(command, Workflow.ForgeWorkflow.Set(context), ForgeJsonContext.Default.JsonSuccessForgeState);
                    break;
                case "run cleanup":
                    if (context.Host == HostKind.Claude)
                        JsonOutput.Success(command, ClaudeCommandHandlers.Cleanup(context), ForgeJsonContext.Default.JsonSuccessCleanupData);
                    else
                    {
                        if (context.Args.Has("legacy"))
                            Workflow.Planning.Materializer.CleanupLegacy(context.Workspace, context.Args.Has("purge-generated-agents"), context.Host);
                        else
                            Workflow.ForgeWorkflow.Cleanup(context.Workspace, context.Args.Has("purge-generated-agents"), context.Host);
                        JsonOutput.Success(command,
                                           new CleanupData(true, context.Args.Has("purge-generated-agents")),
                                           ForgeJsonContext.Default.JsonSuccessCleanupData);
                    }
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

    private static HostPendingHandlers PendingHandler(HostKind host) => PendingHandlers.TryGetValue(host, out var handlers)
                                                                            ? handlers
                                                                            : throw new CliFailure("usage",
                                                                                                   $"pending-run commands do not support --host {host.ToString().ToLowerInvariant()}");

    private static void ValidateOptions(CliCommandDefinition command, ParsedArgs parsed)
    {
        if (parsed.Positionals.Count > 0)
        {
            throw new CliFailure("usage", $"{command.Name} does not accept positional arguments");
        }

        foreach (var name in parsed.Names)
        {
            if (!command.Options.Contains(name)) throw new CliFailure("usage", $"unknown option --{name}");
            if (!CliCommands.IsBoolean(name) && string.Equals(parsed.Get(name), "true", StringComparison.Ordinal))
                throw new CliFailure("usage", $"--{name} requires a value");
        }
    }

    private static void PrintHelp(string command)
    {
        JsonOutput.Success(command,
                           new HelpData($"planforge {command} [options]", CliCommands.Names.Append("hook capture-context").Append("hook claude-workflow").ToList()),
                           ForgeJsonContext.Default.JsonSuccessHelpData);
    }
}
