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
    private const int Attempts = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

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
    public static void Append(string path, string content) => Retry(() =>
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                                          bufferSize: 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, Utf8);
        writer.Write(content);
    });

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

    // A blocked replace surfaces as UnauthorizedAccessException on Windows, not IOException, so
    // both have to be treated as "someone else has it open right now".
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }
}
