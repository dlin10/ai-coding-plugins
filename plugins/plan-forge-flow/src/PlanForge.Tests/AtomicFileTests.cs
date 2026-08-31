using PlanForge.Infrastructure;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The run folder can have more than one writer, because stdio gives one server process per client
/// and a plugin can be registered globally and per repository at once.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-atomic", Guid.NewGuid().ToString("n"));

    public AtomicFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Replaces_contents_and_leaves_no_temporary_behind()
    {
        var path = Path.Combine(_root, "state.json");

        AtomicFile.Write(path, "first");
        AtomicFile.Write(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal(["state.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void Creates_the_directory_it_writes_into()
    {
        var path = Path.Combine(_root, "jobs", "job-01.json");

        AtomicFile.Write(path, "{}");

        Assert.Equal("{}", File.ReadAllText(path));
    }

    /// <summary>
    /// The property that matters: a reader never observes half a write. Plain WriteAllText fails
    /// this, and a half-written state.json reads as a run that does not exist.
    /// </summary>
    [Fact]
    public async Task A_reader_never_observes_a_partial_write()
    {
        var path = Path.Combine(_root, "state.json");
        var payloads = Enumerable.Range(0, 4)
                                 .Select(writer => new string((char)('a' + writer), 64 * 1024))
                                 .ToArray();
        AtomicFile.Write(path, payloads[0]);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var reads = 0;

        var writing = payloads.Select((payload, index) => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) AtomicFile.Write(path, payloads[index]);
        })).ToArray();

        var reading = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var seen = AtomicFile.Read(path);
                Assert.Contains(seen, payloads);
                Interlocked.Increment(ref reads);
            }
        })).ToArray();

        await Task.WhenAll([.. writing, .. reading]);
        Assert.True(reads > 0, "the readers never ran");
    }

    [Fact]
    public void Appends_without_losing_earlier_entries()
    {
        var path = Path.Combine(_root, "review-log.md");

        AtomicFile.Append(path, "## Round 1\n");
        AtomicFile.Append(path, "## Round 2\n");

        Assert.Equal("## Round 1\n## Round 2\n", File.ReadAllText(path));
    }
}
