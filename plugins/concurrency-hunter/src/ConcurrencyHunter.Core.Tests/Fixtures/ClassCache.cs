using System.Collections.Concurrent;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>What one test class computes once and shares between its tests and theory rows. A class declares it as a class fixture,
/// and xUnit releases it when that class finishes; a static field would keep its value, often a whole engine run, until the test
/// run ends, while every other class allocates over it.</summary>
public sealed class ClassCache
{
    private readonly ConcurrentDictionary<string, Lazy<object>> _values = new(StringComparer.Ordinal);

    /// <summary>The value under a key: created once, by the first caller, and shared with every later one.</summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="key">The name of the value within its class.</param>
    /// <param name="create">Creates the value.</param>
    public T Get<T>(string key, Func<T> create) where T : notnull =>
        (T)_values.GetOrAdd(key, _ => new Lazy<object>(() => create(), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
}
