using Microsoft.EntityFrameworkCore;

namespace Demo.Web.Cases.EfAddSharedEntity;

public sealed class Product
{
    public int Stock;
}

/// <summary>A singleton that holds an entity every worker can reach.</summary>
public sealed class Catalog
{
    public readonly Product Featured = new();
}

public sealed class ShopContext : DbContext
{
}

/// <summary>Hands the shared entity to a context of its own: tracking may set any of the entity's fields, which the known call
/// writes where it is made.</summary>
public sealed class ImportWorker : BackgroundService
{
    private readonly Catalog _catalog;

    public ImportWorker(Catalog catalog) => _catalog = catalog;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var context = new ShopContext();
        context.Add(_catalog.Featured);
        context.SaveChanges();
        context.Dispose();
        return Task.CompletedTask;
    }
}

public sealed class StockWorker : BackgroundService
{
    private readonly Catalog _catalog;

    public StockWorker(Catalog catalog) => _catalog = catalog;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _catalog.Featured.Stock = 5;
        return Task.CompletedTask;
    }
}

public static class EfAddSharedEntityCase
{
    public static IServiceCollection AddEfAddSharedEntity(this IServiceCollection services) =>
        services.AddSingleton<Catalog>()
                .AddHostedService<ImportWorker>()
                .AddHostedService<StockWorker>();
}
