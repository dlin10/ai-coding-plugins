namespace Demo.Web.Cases.AwaitConditionalTask;

/// <summary>The result of a conditional expression flows straight into the await, so the tail of whichever branch
/// ran is ordered before the write after it; the same conditional kept in a local first leaves both tails
/// overlapping the write between the call and the await.</summary>
public sealed class ChoiceWorker : BackgroundService
{
    public string? AwaitedFirst { get; private set; }
    public string? AwaitedSecond { get; private set; }
    public string? DeferredFirst { get; private set; }
    public string? DeferredSecond { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await (stoppingToken.IsCancellationRequested ? AwaitedFirstAsync() : AwaitedSecondAsync());
        AwaitedFirst = "parent";
        AwaitedSecond = "parent";

        var deferred = stoppingToken.IsCancellationRequested ? DeferredFirstAsync() : DeferredSecondAsync();
        DeferredFirst = "parent";
        DeferredSecond = "parent";
        await deferred;
    }

    private async Task AwaitedFirstAsync()
    {
        await Task.Yield();
        AwaitedFirst = "first";
    }

    private async Task AwaitedSecondAsync()
    {
        await Task.Yield();
        AwaitedSecond = "second";
    }

    private async Task DeferredFirstAsync()
    {
        await Task.Yield();
        DeferredFirst = "first";
    }

    private async Task DeferredSecondAsync()
    {
        await Task.Yield();
        DeferredSecond = "second";
    }
}

public static class AwaitConditionalTaskCase
{
    public static IServiceCollection AddAwaitConditionalTask(this IServiceCollection services) =>
        services.AddHostedService<ChoiceWorker>();
}
