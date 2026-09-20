namespace Demo.Web.Cases.DictionaryDisjointKeysStructural;

/// <summary>Two workers insert into one plain <see cref="Dictionary{TKey, TValue}"/> under keys that are proven apart. The cells
/// they write are two resources and never meet, but both insertions rewrite the buckets the dictionary keeps its entries in, and
/// that structure is one resource for every key (ADR 0010).</summary>
public sealed class Ledger
{
    public Dictionary<string, int> Entries { get; } = new();

    public void AddFirst() => Entries.Add("first", 1);

    public void AddSecond() => Entries.Add("second", 2);
}

public sealed class FirstLedgerWorker : BackgroundService
{
    private readonly Ledger _ledger;

    public FirstLedgerWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ledger.AddFirst();
        return Task.CompletedTask;
    }
}

public sealed class SecondLedgerWorker : BackgroundService
{
    private readonly Ledger _ledger;

    public SecondLedgerWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ledger.AddSecond();
        return Task.CompletedTask;
    }
}

public static class DictionaryDisjointKeysStructuralCase
{
    public static IServiceCollection AddDictionaryDisjointKeysStructural(this IServiceCollection services) =>
        services.AddSingleton<Ledger>()
                .AddHostedService<FirstLedgerWorker>()
                .AddHostedService<SecondLedgerWorker>();
}
