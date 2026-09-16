namespace Demo.Web.Cases.GenericSingletonPerTypeArgument;

public sealed class Order
{
}

public sealed class Invoice
{
}

/// <summary>One generic singleton per type argument: the two writers of Store&lt;Order&gt; race, while the
/// writer of Store&lt;Invoice&gt; owns a different instance.</summary>
public sealed class Store<T>
{
    public string? LastKey { get; set; }
}

public sealed class OrderWriterA : BackgroundService
{
    private readonly Store<Order> _store;

    public OrderWriterA(Store<Order> store) => _store = store;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.LastKey = "a";
        return Task.CompletedTask;
    }
}

public sealed class OrderWriterB : BackgroundService
{
    private readonly Store<Order> _store;

    public OrderWriterB(Store<Order> store) => _store = store;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.LastKey = "b";
        return Task.CompletedTask;
    }
}

public sealed class InvoiceWriter : BackgroundService
{
    private readonly Store<Invoice> _store;

    public InvoiceWriter(Store<Invoice> store) => _store = store;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.LastKey = "invoice";
        return Task.CompletedTask;
    }
}

public static class GenericSingletonPerTypeArgumentCase
{
    public static IServiceCollection AddGenericSingletonPerTypeArgument(this IServiceCollection services) =>
        services.AddSingleton<Store<Order>>()
                .AddSingleton<Store<Invoice>>()
                .AddHostedService<OrderWriterA>()
                .AddHostedService<OrderWriterB>()
                .AddHostedService<InvoiceWriter>();
}
