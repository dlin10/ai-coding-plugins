namespace ConcurrencyHunter.Heap;

/// <summary>A cancelled engine stage with a snapshot of its counters at the cancellation check.</summary>
public sealed class EngineStageCancelledException : OperationCanceledException
{
    internal EngineStageCancelledException(string stage, IReadOnlyDictionary<string, int> counters, CancellationToken cancellationToken,
                                           OperationCanceledException innerException)
        : base($"The {stage} stage was cancelled.", innerException, cancellationToken)
    {
        Stage = stage;
        Counters = counters;
    }

    public string Stage { get; }
    public IReadOnlyDictionary<string, int> Counters { get; }
}
