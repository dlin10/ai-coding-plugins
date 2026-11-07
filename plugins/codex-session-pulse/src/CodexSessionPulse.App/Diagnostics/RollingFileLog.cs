using System.Globalization;
using System.IO;
using System.Text;

namespace CodexSessionPulse.App.Diagnostics;

/// <summary>
/// The overlay's own log (spec §8.2): <c>overlay.log</c> plus at most four older files of 1 MB each; the hook writes
/// <c>hook.log</c> the same way. It never throws, and callers never pass prompts, rollout text or raw JSON.
/// </summary>
/// <param name="directory">The log folder.</param>
/// <param name="maxBytes">The size at which the current file is rotated.</param>
/// <param name="maxFiles">The number of files kept, the current one included.</param>
/// <param name="baseName">The file name without <c>.log</c>.</param>
internal sealed class RollingFileLog(string directory, long maxBytes = 1024 * 1024, int maxFiles = 5, string baseName = "overlay")
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Lock _gate = new();

    /// <summary>Gets the current log file.</summary>
    public string CurrentFile => Path.Combine(directory, baseName + ".log");

    /// <summary>Writes an informational line.</summary>
    /// <param name="message">The message.</param>
    public void Info(string message) => Write("INFO", message);

    /// <summary>Writes a warning.</summary>
    /// <param name="message">The message.</param>
    public void Warn(string message) => Write("WARN", message);

    /// <summary>Writes an error with the exception type and message, not its data.</summary>
    /// <param name="message">The message.</param>
    /// <param name="exception">The exception.</param>
    public void Error(string message, Exception exception) => Write("ERROR", $"{message}: {exception.GetType().Name}: {exception.Message}");

    private void Write(string level, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level} {message}{Environment.NewLine}");

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                FileInfo current = new(CurrentFile);

                if (current.Exists && current.Length + Utf8NoBom.GetByteCount(line) > maxBytes)
                {
                    Rotate();
                }

                File.AppendAllText(CurrentFile, line, Utf8NoBom);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the overlay down.
            }
        }
    }

    private void Rotate()
    {
        File.Delete(Numbered(maxFiles - 1));

        for (int index = maxFiles - 2; index >= 1; index--)
        {
            if (File.Exists(Numbered(index)))
            {
                File.Move(Numbered(index), Numbered(index + 1));
            }
        }

        File.Move(CurrentFile, Numbered(1));
    }

    private string Numbered(int index) => Path.Combine(directory, $"{baseName}.{index}.log");
}
