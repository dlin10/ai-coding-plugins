using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PlanForge.Infrastructure;

/// <summary>
/// Write-then-rename for the run folder. More than one server process can be writing it: stdio
/// gives one process per client, so every session has its own, and a host can have the plugin
/// registered globally and per repository at the same time. A torn <c>state.json</c> does not
/// read as a damaged run — it reads as no run at all.
/// </summary>
/// <remarks>
/// The atomic core of the 1.x DurableFiles, without its symlink and reparse-point guards: those
/// were replaced by the single containment check in <see cref="Run.RunDirectory"/>.
/// </remarks>
internal static class AtomicFile
{
    /// <summary>
    /// How long a caller waits out a file someone else has open. A span rather than a count of
    /// attempts, because what has to be covered is how long the contention lasts: a two-core
    /// runner is where it lasts longest and where a fixed count of probes is spent soonest.
    /// </summary>
    private static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a caller waits for a file that is not there. Windows implements
    /// <see cref="File.Replace(string,string,string)"/> by taking the destination away and renaming
    /// the replacement into its place, so a path can be missing for the moment that takes — but a
    /// file that is genuinely gone stays gone, and every further millisecond is charged to a caller
    /// who already has its answer.
    /// </summary>
    private static readonly TimeSpan ReplaceWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>The ceiling on one wait; the wait itself is a random part of it — see <see cref="Retry"/>.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The per-file gate <see cref="Append"/> queues on, keyed the way Windows names files.</summary>
    private static readonly ConcurrentDictionary<string, object> Appenders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a file that <see cref="Write"/> may replace underneath. The delete share is the whole
    /// point: without it Windows refuses the replacing rename while this handle is open, so an
    /// ordinary File.ReadAllText turns a concurrent writer into a failure instead of a wait. With
    /// it, the writer proceeds and this handle keeps serving the contents it opened — one whole
    /// version, never a mixture.
    /// </summary>
    public static string Read(string path)
    {
        string text = string.Empty;
        Retry(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8);
            text = reader.ReadToEnd();
        });

        return text;
    }

    /// <summary>Replaces the file's contents, or leaves the previous contents untouched.</summary>
    public static void Write(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
                        ?? throw new InvalidOperationException($"{path} has no directory");
        Directory.CreateDirectory(directory);

        // A per-writer temp name, so two processes never collide on the intermediate file.
        var temp = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():n}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                               bufferSize: 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Utf8))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            Retry(() => Swap(temp, path));
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Appends under an exclusive handle. Two processes appending at once would otherwise be free
    /// to interleave inside one entry, and the review log is read back as a critic's input.
    /// </summary>
    /// <remarks>
    /// One appender at a time per file within this process, because the exclusive handle is granted
    /// without a queue: a loser is told the file is busy and has to come back, so where several
    /// threads append to one file the same thread can lose every attempt it is given while the
    /// others make progress. That is not hypothetical — a run's <c>forge.log</c> is written by every
    /// flow that finds it through <c>RunLog.Current</c>, and an append that gives up there is an
    /// entry lost in silence, since a failed write may not take the tool call down with it. Ordering
    /// them here leaves <see cref="Retry"/> only the contention it cannot order: the other server
    /// process, which is a second writer rather than every thread of this one.
    /// </remarks>
    public static void Append(string path, string content)
    {
        lock (Appenders.GetOrAdd(Path.GetFullPath(path), _ => new object()))
        {
            Retry(() =>
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                                                  bufferSize: 4096, FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream, Utf8);
                writer.Write(content);
            });
        }
    }

    /// <summary>
    /// Measured on Windows 11 rather than assumed: <c>File.Move(overwrite: true)</c> refuses to
    /// replace a file that anyone has open, whatever share mode that reader asked for, while
    /// <c>File.Replace</c> succeeds against a reader holding <see cref="FileShare.Delete"/> — which
    /// is why <see cref="Read"/> asks for it. Replace needs the target to exist, so the first write
    /// is an ordinary move.
    /// </summary>
    private static void Swap(string temp, string path)
    {
        if (!File.Exists(path))
        {
            File.Move(temp, path, overwrite: true);
            return;
        }

        File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
    }

    private static void Retry(Action action)
    {
        var waited = Stopwatch.StartNew();
        var ceiling = 1.0;

        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (Exception error) when (Waits(error, waited.Elapsed))
            {
                // A random part of the ceiling rather than the ceiling itself. Every loser of a
                // contended open used to wake on one fixed cadence and collide with the same
                // winners again, so a waiter could lose every attempt it was given while the
                // others made progress — and an append that gives up is an entry lost, since
                // RunLog.Write may not let a failed write take the call down with it. Waking at
                // an unshared moment is what makes the wait fair rather than another collision.
                Thread.Sleep(TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling));
                ceiling = Math.Min(ceiling * 2, MaxRetryDelay.TotalMilliseconds);
            }
        }
    }

    /// <summary>
    /// Whether the failure is one that coming back later can fix, and whether the caller has waited
    /// long enough already.
    /// </summary>
    /// <remarks>
    /// A blocked replace surfaces as <see cref="UnauthorizedAccessException"/> on Windows rather
    /// than <see cref="IOException"/>, so both mean "someone else has it open right now". The two
    /// absences do not: a missing file may be a replacement mid-flight and waits only that long
    /// (<see cref="ReplaceWindow"/>), and a missing directory is nothing in flight at all — nothing
    /// here removes one — so it answers at once rather than charging a caller for a wait that
    /// cannot end well.
    /// </remarks>
    private static bool Waits(Exception error, TimeSpan waited) => error switch
                                                                  {
                                                                      DirectoryNotFoundException => false,
                                                                      FileNotFoundException => waited < ReplaceWindow,
                                                                      IOException or UnauthorizedAccessException => waited < RetryBudget,
                                                                      _ => false
                                                                  };
}
