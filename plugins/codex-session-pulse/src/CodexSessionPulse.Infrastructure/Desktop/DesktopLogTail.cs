using System.Globalization;
using System.Text.RegularExpressions;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.Infrastructure.Desktop;

/// <summary>The Desktop log reader's state for "Copy diagnostics"; it holds no line text and no thread id.</summary>
/// <param name="FileFound">Whether a log file of the followed Codex process was found.</param>
/// <param name="Markers">The route lines read since the file was opened.</param>
/// <param name="Malformed">Marker lines that did not parse: a sign that Codex changed the format.</param>
/// <param name="LastKind">The kind of the latest route, or <see langword="null"/> before the first.</param>
/// <param name="LastRouteAtUtc">When Codex logged the latest route, in UTC.</param>
/// <param name="LastError">The exception type of the last failed open or read, or <see langword="null"/>.</param>
public sealed record DesktopLogDiagnostics(bool FileFound,
                                           long Markers,
                                           long Malformed,
                                           CodexRouteKind? LastKind,
                                           DateTimeOffset? LastRouteAtUtc,
                                           string? LastError);

/// <summary>
/// Follows the log of one Codex Desktop process and keeps the latest <see cref="CodexRoute"/> it names (see
/// <see cref="DesktopLogLine"/>). Called from one thread at a time.
/// </summary>
/// <remarks>
/// <para>The file is <c>&lt;logs root&gt;\yyyy\MM\dd\codex-desktop-&lt;session&gt;-&lt;PID&gt;-t0-i1-&lt;hhmmss&gt;-&lt;n&gt;.log</c>,
/// the date and time in UTC. Only <c>t0</c> is the main log; a <c>t1</c> file of the same PID, created later, holds a
/// few native-module lines and no route. The process starts a new file when one grows large (<c>-1</c>, <c>-2</c>) and
/// at UTC midnight in the next day's folder, so the newest <c>t0</c> file of the PID is looked for every
/// <see cref="LocateInterval"/>. A newly opened file is read from its start, which finds the page shown before the
/// overlay started.</para>
/// <para>Codex keeps the file open: its directory entry shows neither the size nor the time of the writes, and a file
/// watcher hears nothing. So the length is read through an open handle, shared for read, write and delete, on every
/// <see cref="Poll"/>. Lines are split on the <c>\n</c> byte; a partly written line waits for its end, and a line longer
/// than <see cref="MaxLineBytes"/> is skipped without being held.</para>
/// </remarks>
public sealed partial class DesktopLogTail : IDisposable
{
    /// <summary>The longest line that is buffered; the route lines are about 300 bytes.</summary>
    public const int MaxLineBytes = 64 * 1024;

    /// <summary>How often the newest log file of the process is looked for.</summary>
    public static readonly TimeSpan LocateInterval = TimeSpan.FromSeconds(5);

    private const int ReadBufferBytes = 64 * 1024;
    private const int DaysBack = 2;

    private readonly string _logsRoot;
    private readonly int _processId;
    private readonly IClock _clock;
    private readonly byte[] _buffer = new byte[ReadBufferBytes];
    private readonly byte[] _partial = new byte[MaxLineBytes];
    private int _partialLength;
    private bool _skippingLine;
    private FileStream? _stream;
    private string? _path;
    private long _offset;
    private DateTimeOffset? _lastLocateUtc;
    private long _markers;
    private long _malformed;
    private string? _lastError;

    /// <summary>Creates a reader; the first <see cref="Poll"/> looks for the file.</summary>
    /// <param name="logsRoot">Codex Desktop's <c>Logs</c> folder (<see cref="LogsRoot"/>).</param>
    /// <param name="processId">The PID of Codex's main process, the one that owns its windows.</param>
    /// <param name="clock">The clock that dates the file search.</param>
    public DesktopLogTail(string logsRoot, int processId, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        ArgumentNullException.ThrowIfNull(clock);
        _logsRoot = logsRoot;
        _processId = processId;
        _clock = clock;
    }

    /// <summary>Gets the latest route read, or <see langword="null"/> before the first.</summary>
    public CodexRoute? Route { get; private set; }

    /// <summary>
    /// Gets Codex Desktop's <c>Logs</c> folder. An MSIX install writes it into its package's <c>LocalCache</c>, which is
    /// where any process reads it, the overlay started from a hook inside the package's file redirection included.
    /// </summary>
    /// <param name="localAppData">The real <c>%LOCALAPPDATA%</c>.</param>
    /// <param name="packageFamilyName">Codex's package family, or <see langword="null"/> for an install without a package.</param>
    /// <returns>The folder; it may not exist.</returns>
    public static string LogsRoot(string localAppData, string? packageFamilyName)
    {
        return packageFamilyName is null
            ? Path.Combine(localAppData, "Codex", "Logs")
            : Path.Combine(localAppData, "Packages", packageFamilyName, "LocalCache", "Local", "Codex", "Logs");
    }

    /// <summary>
    /// Reads what the log appended since the last poll, after looking for a newer file when due.
    /// </summary>
    /// <returns>Whether the route's page or thread changed; a repeat of the same route is not a change.</returns>
    public bool Poll()
    {
        DateTimeOffset now = _clock.UtcNow;
        CodexRoute? before = Route;

        if (_stream is null || _lastLocateUtc is not { } last || now - last >= LocateInterval || now < last)
        {
            _lastLocateUtc = now;
            Locate(now);
        }

        if (_stream is not null)
        {
            try
            {
                ReadAppended();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Codex rotated or deleted the file mid-read; the next poll looks for it again.
                _lastError = exception.GetType().Name;
                Close();
            }
        }

        return !SamePage(before, Route);
    }

    /// <summary>Describes the reader for diagnostics.</summary>
    /// <returns>The state.</returns>
    public DesktopLogDiagnostics GetDiagnostics()
    {
        return new DesktopLogDiagnostics(_path is not null, _markers, _malformed, Route?.Kind, Route?.AtUtc, _lastError);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Close();
    }

    /// <summary>
    /// Finds the newest log file of a Codex process: the PID is the number after the session id in the name. The most
    /// recent UTC day folder that has one wins; within it, the latest created, then the greatest name.
    /// </summary>
    /// <param name="logsRoot">Codex Desktop's <c>Logs</c> folder.</param>
    /// <param name="processId">The PID of Codex's main process.</param>
    /// <param name="nowUtc">The current time; today's and the two previous UTC day folders are searched.</param>
    /// <returns>The file, or <see langword="null"/> when there is none.</returns>
    private static string? FindLogFile(string logsRoot, int processId, DateTimeOffset nowUtc)
    {
        string pid = processId.ToString(CultureInfo.InvariantCulture);

        for (int day = 0; day <= DaysBack; day++)
        {
            DateTime date = nowUtc.UtcDateTime.Date.AddDays(-day);
            string folder = Path.Combine(logsRoot, date.ToString("yyyy", CultureInfo.InvariantCulture),
                                         date.ToString("MM", CultureInfo.InvariantCulture), date.ToString("dd", CultureInfo.InvariantCulture));
            FileInfo? newest = null;

            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("codex-desktop-*.log"))
            {
                Match match = FileNamePattern().Match(file.Name);

                if (match.Success && match.Groups["pid"].Value == pid
                    && (newest is null || file.CreationTimeUtc > newest.CreationTimeUtc
                        || (file.CreationTimeUtc == newest.CreationTimeUtc && string.CompareOrdinal(file.Name, newest.Name) > 0)))
                {
                    newest = file;
                }
            }

            if (newest is not null)
            {
                return newest.FullName;
            }
        }

        return null;
    }

    private static bool SamePage(CodexRoute? a, CodexRoute? b) =>
        a is null ? b is null : b is not null && a.Kind == b.Kind && a.ThreadId == b.ThreadId;

    private void Locate(DateTimeOffset nowUtc)
    {
        string? path;

        try
        {
            path = FindLogFile(_logsRoot, _processId, nowUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _lastError = exception.GetType().Name;
            return;
        }

        if (path is null || (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) && _stream is not null))
        {
            return;
        }

        Close();

        try
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0);
            _path = path;
            _offset = 0;
            _markers = 0;
            _malformed = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _lastError = exception.GetType().Name;
        }
    }

    private void Close()
    {
        _stream?.Dispose();
        _stream = null;
        _partialLength = 0;
        _skippingLine = false;
    }

    private void ReadAppended()
    {
        long length = _stream!.Length;

        if (length < _offset)
        {
            // The file was cut back or replaced under the same name: read it again from the start.
            _offset = 0;
            _partialLength = 0;
            _skippingLine = false;
        }

        _stream.Position = _offset;

        while (_offset < length)
        {
            int read = _stream.Read(_buffer, 0, (int)Math.Min(_buffer.Length, length - _offset));

            if (read == 0)
            {
                break;
            }

            _offset += read;
            Consume(_buffer.AsSpan(0, read));
        }
    }

    private void Consume(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int end = data.IndexOf((byte)'\n');
            ReadOnlySpan<byte> piece = end < 0 ? data : data[..end];

            if (!_skippingLine)
            {
                if (_partialLength + piece.Length > MaxLineBytes)
                {
                    _skippingLine = true;
                    _partialLength = 0;
                }
                else
                {
                    piece.CopyTo(_partial.AsSpan(_partialLength));
                    _partialLength += piece.Length;
                }
            }

            if (end < 0)
            {
                return;
            }

            if (!_skippingLine)
            {
                HandleLine(_partial.AsSpan(0, _partialLength));
            }

            _partialLength = 0;
            _skippingLine = false;
            data = data[(end + 1)..];
        }
    }

    private void HandleLine(ReadOnlySpan<byte> line)
    {
        if (!line.IsEmpty && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (!DesktopLogLine.IsMarker(line))
        {
            return;
        }

        if (!DesktopLogLine.TryParse(line, out CodexRoute route))
        {
            _malformed++;
            return;
        }

        _markers++;

        // Codex repeats the line up to 8 times per switch; the first one dates the page.
        if (!SamePage(Route, route))
        {
            Route = route;
        }
    }

    [GeneratedRegex(@"^codex-desktop-[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}-(?<pid>\d+)-t0-i\d+-\d{6}-\d+\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
