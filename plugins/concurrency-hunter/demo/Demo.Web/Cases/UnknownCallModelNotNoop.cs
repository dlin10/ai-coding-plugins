using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.UnknownCallModelNotNoop;

/// <summary>A singleton whose list a worker hands to a member of the BCL and an action counts.</summary>
public sealed class Inbox
{
    public readonly List<string> Items = new();
}

/// <summary>Empties the list through <c>CollectionsMarshal.SetCount</c>, a member neither the library table (TD-034a) nor the collection
/// table (ADR 0010) describes. The call is no no-op: its unknown effect may read and write the list's structure, conflicts like a write
/// with the action's count, and the call is the semantic gap that decides the operation (TD-025, TD-034, TD-039).</summary>
public sealed class InboxTrimmer : BackgroundService
{
    private readonly Inbox _inbox;

    public InboxTrimmer(Inbox inbox) => _inbox = inbox;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CollectionsMarshal.SetCount(_inbox.Items, 0);
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/unknown-call-model-not-noop")]
public sealed class InboxController : ControllerBase
{
    private readonly Inbox _inbox;

    public InboxController(Inbox inbox) => _inbox = inbox;

    [HttpGet]
    public int Get() => _inbox.Items.Count;
}

public static class UnknownCallModelNotNoopCase
{
    public static IServiceCollection AddUnknownCallModelNotNoop(this IServiceCollection services) =>
        services.AddSingleton<Inbox>()
                .AddHostedService<InboxTrimmer>();
}
