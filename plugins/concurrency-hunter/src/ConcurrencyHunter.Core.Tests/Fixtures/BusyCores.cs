namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>A busy thread on every core for as long as it lives: the machine's load a test asks an answer not to depend
/// on (ADR 0021).</summary>
internal sealed class BusyCores : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread[] _threads;

    public BusyCores()
    {
        _threads = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(Spin) { IsBackground = true }).ToArray();
        foreach (var thread in _threads)
            thread.Start();
    }

    private void Spin()
    {
        var count = 0L;
        while (!_stop.IsCancellationRequested)
            count++;
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var thread in _threads)
            thread.Join();
        _stop.Dispose();
    }
}
