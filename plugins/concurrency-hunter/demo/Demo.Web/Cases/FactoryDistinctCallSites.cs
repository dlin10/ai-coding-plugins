namespace Demo.Web.Cases.FactoryDistinctCallSites;

public sealed class Label
{
    public string? Text { get; set; }
}

/// <summary>Hard negative: one <c>new</c> inside a static factory, two call sites. Each call returns a fresh
/// object, so the static method's call-site context must keep the two apart.</summary>
public static class LabelFactory
{
    public static Label Create() => new();
}

public sealed class FirstLabelWorker : BackgroundService
{
    private readonly Label _label = LabelFactory.Create();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _label.Text = "first";
        return Task.CompletedTask;
    }
}

public sealed class SecondLabelWorker : BackgroundService
{
    private readonly Label _label = LabelFactory.Create();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _label.Text = "second";
        return Task.CompletedTask;
    }
}

public static class FactoryDistinctCallSitesCase
{
    public static IServiceCollection AddFactoryDistinctCallSites(this IServiceCollection services) =>
        services.AddHostedService<FirstLabelWorker>()
                .AddHostedService<SecondLabelWorker>();
}
