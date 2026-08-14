using PlanForgeFlow.Cli;

namespace PlanForgeFlow.OpenAI;

internal sealed record AppServerAgentResult(string ThreadId,
                                            string TurnId,
                                            string Status,
                                            string Model,
                                            string Effort,
                                            IReadOnlyList<AppServerNotification> Notifications);

internal static class AppServerAgents
{
    public static AppServerAgentResult RunFreshReviewer(AppServerClient client,
                                                         string workspace,
                                                         string model,
                                                         string effort,
                                                         string prompt,
                                                         Func<bool>? cancellationRequested = null,
                                                         Action? heartbeat = null)
    {
        var selection = Prepare(client, model, effort);
        var thread = client.StartThread(workspace, selection);
        AppServerAgentResult result;
        try
        {
            var started = client.StartTurn(thread.Id, prompt, workspace, selection, effort, writable: false);
            var completed = client.WaitForTurn(thread.Id, started.Id, cancellationRequested, heartbeat);
            if (completed.Status != "completed") throw new CliFailure("agent", $"OpenAI reviewer ended with {completed.Status}: {completed.Error}", 3);
            var audited = client.ReadThread(thread.Id);
            if (audited.Status is not ("idle" or "notLoaded")) throw new CliFailure("identity", "OpenAI reviewer thread is not idle after audit", 3);
            result = new AppServerAgentResult(thread.Id, completed.Id, completed.Status, selection.Id, effort, client.Notifications.ToArray());
        }
        catch
        {
            client.DeleteThreadBestEffort(thread.Id, heartbeat);
            throw;
        }
        client.DeleteThread(thread.Id, heartbeat);
        return result;
    }

    public static AppServerAgentResult CreateBuilderHold(AppServerClient client,
                                                          string workspace,
                                                          string model,
                                                          string effort,
                                                          string prompt,
                                                          Func<bool>? cancellationRequested = null,
                                                          Action? heartbeat = null)
    {
        var selection = Prepare(client, model, effort);
        var thread = client.StartThread(workspace, selection);
        var started = client.StartTurn(thread.Id, prompt, workspace, selection, effort, writable: false);
        var completed = client.WaitForTurn(thread.Id, started.Id, cancellationRequested, heartbeat);
        if (completed.Status != "completed") throw new CliFailure("agent", $"OpenAI builder hold ended with {completed.Status}: {completed.Error}", 3);
        return new AppServerAgentResult(thread.Id, completed.Id, completed.Status, selection.Id, effort, client.Notifications.ToArray());
    }

    public static AppServerAgentResult ResumeBuilder(AppServerClient client,
                                                      string threadId,
                                                      string workspace,
                                                      string model,
                                                      string effort,
                                                      string prompt,
                                                      Func<bool>? cancellationRequested = null,
                                                      Action? heartbeat = null)
    {
        var selection = Prepare(client, model, effort);
        var stored = client.ReadThread(threadId);
        if (stored.Status == "systemError") throw new CliFailure("identity", "OpenAI builder thread is in systemError state", 3);
        client.ResumeThread(threadId, workspace, selection, writable: true);
        var started = client.StartTurn(threadId, prompt, workspace, selection, effort, writable: true);
        try
        {
            var completed = client.WaitForTurn(threadId, started.Id, cancellationRequested, heartbeat);
            if (completed.Status != "completed") throw new CliFailure("agent", $"OpenAI builder ended with {completed.Status}: {completed.Error}", 3);
            return new AppServerAgentResult(threadId, completed.Id, completed.Status, selection.Id, effort, client.Notifications.ToArray());
        }
        catch (OperationCanceledException) { throw; }
    }

    private static AppServerModel Prepare(AppServerClient client, string model, string effort)
    {
        client.Initialize();
        client.ValidateOpenAiAccount();
        return AppServerContracts.SelectModel(client.ListModels(), model, effort);
    }
}
