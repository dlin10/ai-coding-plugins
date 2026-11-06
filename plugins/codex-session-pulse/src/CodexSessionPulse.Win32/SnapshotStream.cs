namespace CodexSessionPulse.Win32;

/// <summary>A minimal hot observable that replays the latest value to each new subscriber. Not thread-safe: publish from one thread.</summary>
/// <typeparam name="T">The value type.</typeparam>
internal sealed class SnapshotStream<T> : IObservable<T>
    where T : class
{
    private readonly List<IObserver<T>> _observers = [];
    private T? _latest;

    /// <summary>Delivers a value to every subscriber and keeps it for later ones.</summary>
    /// <param name="value">The value.</param>
    public void Publish(T value)
    {
        _latest = value;

        foreach (IObserver<T> observer in _observers.ToArray())
        {
            observer.OnNext(value);
        }
    }

    /// <summary>Completes every subscriber.</summary>
    public void Complete()
    {
        foreach (IObserver<T> observer in _observers.ToArray())
        {
            observer.OnCompleted();
        }

        _observers.Clear();
    }

    /// <inheritdoc />
    public IDisposable Subscribe(IObserver<T> observer)
    {
        _observers.Add(observer);

        if (_latest is not null)
        {
            observer.OnNext(_latest);
        }

        return new Subscription(this, observer);
    }

    /// <summary>Removes its observer when disposed.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="observer">The observer.</param>
    private sealed class Subscription(SnapshotStream<T> stream, IObserver<T> observer) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => stream._observers.Remove(observer);
    }
}
