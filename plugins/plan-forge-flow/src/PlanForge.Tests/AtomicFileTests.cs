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

        // A thread each rather than the thread pool's. These loops never yield, and eight of them
        // on a pool sized to the processor count leave the four queued last waiting on thread
        // injection — which arrives about twice a second, well inside the two the loop runs for.
        // The readers then never run at all, and the assertion below reports the scheduler rather
        // than anything this file does.
        var writing = payloads.Select((payload, index) => LongRunning(() =>
        {
            while (!stop.IsCancellationRequested) AtomicFile.Write(path, payloads[index]);
        })).ToArray();

        var reading = Enumerable.Range(0, 4).Select(_ => LongRunning(() =>
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

    /// <summary>
    /// Every entry lands, however many threads are appending. The exclusive handle an append needs
    /// is granted without a queue, so the losers of one open come back and collide again; before
    /// <see cref="AtomicFile.Append"/> ordered them, one thread could lose every attempt it had and
    /// give up while the others made progress. What made that silent rather than loud is
    /// <c>RunLog.Write</c>, which may not let a failed write take the tool call down with it.
    /// </summary>
    /// <remarks>
    /// A load this size passes either way on an unloaded machine — the losing thread was found by
    /// a stress harness at sixty-four writers, not by this. It is here to fail loudly if the
    /// ordering is ever removed under a load a build agent can produce.
    /// </remarks>
    private static Task LongRunning(Action loop) =>
        Task.Factory.StartNew(loop, CancellationToken.None,
                              TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [Fact]
    public async Task Every_concurrent_append_lands()
    {
        const int Writers = 16;
        const int Each = 25;
        var path = Path.Combine(_root, "forge.log");

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(() =>
        {
            for (var entry = 0; entry < Each; entry++)
                AtomicFile.Append(path, $"{writer}-{entry} " + new string('x', 2000) + "\n");
        })));

        Assert.Equal(Writers * Each, File.ReadAllLines(path).Length);
    }
}
