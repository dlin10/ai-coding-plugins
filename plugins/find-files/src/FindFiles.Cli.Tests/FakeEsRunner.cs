using FindFiles.Everything;

namespace FindFiles.Tests;

/// <summary>
/// Replays canned <c>es.exe</c> invocations and records the argument lists it was asked to run, so
/// the exit-code contract can be tested without Everything being installed.
/// </summary>
internal sealed class FakeEsRunner : IEsRunner
{
    private readonly Queue<EsInvocation> _responses = new();

    internal List<IReadOnlyList<string>> Invocations { get; } = [];

    internal FakeEsRunner Enqueue(int exitCode, IEnumerable<string>? lines = null, string standardError = "")
    {
        _responses.Enqueue(new EsInvocation(exitCode, lines?.ToList() ?? [], standardError));
        return this;
    }

    public EsInvocation Run(IReadOnlyList<string> arguments)
    {
        Invocations.Add(arguments);
        return _responses.Count > 0
            ? _responses.Dequeue()
            : throw new InvalidOperationException("FakeEsRunner ran out of queued responses");
    }
}
