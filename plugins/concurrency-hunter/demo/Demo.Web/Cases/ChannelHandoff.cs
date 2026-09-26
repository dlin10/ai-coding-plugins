using System.Threading.Channels;

namespace Demo.Web.Cases.ChannelHandoff;

public sealed class Order
{
    public int Quantity;
}

/// <summary>A singleton that holds the channel and the last order written into it, which makes that order shared.</summary>
public sealed class Mailbox
{
    public readonly Channel<Order> Orders = Channel.CreateUnbounded<Order>();
    public Order? Last;
}

/// <summary>Writes an order into the channel and goes on changing it. The write hands a shared object to a call the analysis cannot
/// follow, so it is a semantic gap in coverage (TD-034); its unknown effect runs in this worker only, and nothing else touches the
/// order.</summary>
public sealed class Producer : BackgroundService
{
    private readonly Mailbox _mailbox;

    public Producer(Mailbox mailbox) => _mailbox = mailbox;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var order = new Order();
        _mailbox.Last = order;
        _mailbox.Orders.Writer.TryWrite(order);
        order.Quantity = 2;
        return Task.CompletedTask;
    }
}

/// <summary>Reads an order out of the channel. What the read returns is tied to nothing the analysis knows (open question 7), so the
/// consumer's read of it touches no resource, and there is no pair with the producer: the gap is what stands for it.</summary>
public sealed class Consumer : BackgroundService
{
    private readonly Mailbox _mailbox;

    public Consumer(Mailbox mailbox) => _mailbox = mailbox;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var order = await _mailbox.Orders.Reader.ReadAsync(stoppingToken);
        _ = order.Quantity;
    }
}

public static class ChannelHandoffCase
{
    public static IServiceCollection AddChannelHandoff(this IServiceCollection services) =>
        services.AddSingleton<Mailbox>()
                .AddHostedService<Producer>()
                .AddHostedService<Consumer>();
}
