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

    /// <summary>
    /// The property four tests were leaning on without asking for it. An append shares
    /// <see cref="FileShare.Read"/>, and an ordinary read shares only <c>Read</c> — which is not
    /// enough to coexist with the writer's <c>Write</c> access, so <c>File.ReadAllText</c> throws
    /// while the handle is open. <see cref="AtomicFile.Read"/> asks for <c>ReadWrite | Delete</c>
    /// and does not. It matters because <c>RunLog.Current</c> falls back to the last log any tool
    /// call served, so the appender to a run's `forge.log` can be a wholly unrelated flow.
    /// </summary>
    [Fact]
    public void A_reader_gets_in_while_an_append_holds_the_file_and_an_ordinary_one_does_not()
    {
        var path = Path.Combine(_root, "forge.log");
        AtomicFile.Append(path, "{\"event\":\"first\"}\n");

        using var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

        Assert.Equal("{\"event\":\"first\"}\n", AtomicFile.Read(path));
        Assert.ThrowsAny<IOException>(() => File.ReadAllText(path));
    }
}
