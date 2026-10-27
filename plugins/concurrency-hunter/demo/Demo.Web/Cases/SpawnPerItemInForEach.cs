namespace Demo.Web.Cases.SpawnPerItemInForEach;

/// <summary>A list's <c>ForEach</c> runs its delegate once per item, in the worker's own execution: the work each run starts
/// overlaps the work the other items start, and the write a run makes before it starts its work meets the work of an earlier
/// item, as in a <c>foreach</c> loop.</summary>
public sealed class ItemsWorker : BackgroundService
{
    private readonly List<int> _items = new() { 1, 2, 3 };

    public int Last { get; private set; }

    public int Marker { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _items.ForEach(item => Task.Run(() => Publish(item)));
        _items.ForEach(Mark);
        return Task.CompletedTask;
    }

    private void Publish(int item) => Last = item;

    private void Mark(int item)
    {
        Marker = 0;
        Task.Run(() => Stamp(item));
    }

    private void Stamp(int item) => Marker = item;
}

public static class SpawnPerItemInForEachCase
{
    public static IServiceCollection AddSpawnPerItemInForEach(this IServiceCollection services) =>
        services.AddHostedService<ItemsWorker>();
}
