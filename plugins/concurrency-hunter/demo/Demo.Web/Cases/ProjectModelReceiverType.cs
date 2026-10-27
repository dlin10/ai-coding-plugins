namespace Demo.Web.Cases.ProjectModelReceiverType;

public sealed class Payload
{
    public int Count;
}

public sealed class Shared
{
    public readonly Payload ViaStringWriter = new Payload();
    public readonly Payload ViaTextWriter = new Payload();
}

public sealed class WritingWorker : BackgroundService
{
    private readonly Shared _shared;

    public WritingWorker(Shared shared) => _shared = shared;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WriteViaStringWriter();
        WriteViaTextWriter();
        return Task.CompletedTask;
    }

    private void WriteViaStringWriter()
    {
        StringWriter sw = new StringWriter();
        sw.Write(_shared.ViaStringWriter);
    }

    private void WriteViaTextWriter()
    {
        TextWriter tw = new StringWriter();
        tw.Write(_shared.ViaTextWriter);
    }
}

public sealed class ReadingWorker : BackgroundService
{
    private readonly Shared _shared;

    public ReadingWorker(Shared shared) => _shared = shared;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = _shared.ViaStringWriter.Count;
        _ = _shared.ViaTextWriter.Count;
        return Task.CompletedTask;
    }
}

public static class ProjectModelReceiverTypeCase
{
    public static IServiceCollection AddProjectModelReceiverType(this IServiceCollection services) =>
        services.AddSingleton<Shared>()
                .AddHostedService<WritingWorker>()
                .AddHostedService<ReadingWorker>();
}
