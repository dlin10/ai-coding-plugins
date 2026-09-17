using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LocatorTransientDistinct;

public sealed class Ticket
{
    public int Number { get; set; }
}

/// <summary>A transient resolved through <see cref="IServiceProvider"/> is a new instance on every call:
/// the action and the worker never write the same object.</summary>
[ApiController]
[Route("cases/locator-transient-distinct")]
public sealed class TicketController : ControllerBase
{
    private readonly IServiceProvider _services;

    public TicketController(IServiceProvider services) => _services = services;

    [HttpPut]
    public void Put(int number) => _services.GetRequiredService<Ticket>().Number = number;
}

public sealed class TicketWorker : BackgroundService
{
    private readonly IServiceProvider _services;

    public TicketWorker(IServiceProvider services) => _services = services;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _services.GetRequiredService<Ticket>().Number = 0;
        return Task.CompletedTask;
    }
}

public static class LocatorTransientDistinctCase
{
    public static IServiceCollection AddLocatorTransientDistinct(this IServiceCollection services) =>
        services.AddTransient<Ticket>()
                .AddHostedService<TicketWorker>();
}
