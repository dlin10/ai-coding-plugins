namespace CodexSessionPulse.Core;

/// <summary>
/// A thread-safe "latest value" publisher (hand-rolled, no Rx): a new subscriber first gets the current value,
/// then every published one, in publish order.
/// </summary>
/// <remarks>
/// Values are delivered under a lock, so delivery is ordered and never interleaves; an observer must return
/// quickly (the overlay marshals to its dispatcher with <c>BeginInvoke</c>) and must not publish from
/// <see cref="IObserver{T}.OnNext"/>. Unsubscribing from inside <see cref="IObserver{T}.OnNext"/> is allowed.
/// </remarks>
/// <typeparam name="T">The value type.</typeparam>
public sealed class SnapshotPublisher<T> : IObservable<T>
    where T : class
{
    private readonly Lock _gate = new();
    private IObserver<T>[] _observers = [];
    private T? _current;

    /// <summary>Gets the latest published value, or <see langword="null"/> before the first one.</summary>
    public T? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Stores the value as current and delivers it to every observer.</summary>
    /// <param name="value">The value.</param>
    public void Publish(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            _current = value;

            foreach (IObserver<T> observer in _observers)
            {
                observer.OnNext(value);
            }
        }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            _observers = [.. _observers, observer];

            if (_current is not null)
            {
                observer.OnNext(_current);
            }
        }

        return new Subscription(this, observer);
    }

    private void Unsubscribe(IObserver<T> observer)
    {
        lock (_gate)
        {
            _observers = Array.FindAll(_observers, o => !ReferenceEquals(o, observer));
        }
    }

    private sealed class Subscription(SnapshotPublisher<T> owner, IObserver<T> observer) : IDisposable
    {
        public void Dispose()
        {
            owner.Unsubscribe(observer);
        }
    }
}
