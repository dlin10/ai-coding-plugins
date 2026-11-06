using System.Buffers;
using System.Collections.Concurrent;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.Infrastructure.Rollouts;

/// <summary>What one poll found, in file order.</summary>
/// <param name="Observations">Threads seen in a <c>session_meta</c> for the first time by this reader.</param>
/// <param name="Usages">New usage events.</param>
public sealed record RolloutPollResult(IReadOnlyList<ThreadObservation> Observations, IReadOnlyList<TokenUsageSnapshot> Usages);

/// <summary>
/// The synchronous core of the rollout reader (spec §5.2): bounded discovery, a byte cursor per file and
/// bounded replay protection. <see cref="Poll"/> is called from one thread at a time; <see cref="MarkChanged"/>
/// and <see cref="RequestRescan"/> may be called from any thread (the file watcher's).
/// </summary>
/// <remarks>
/// <para>Files are opened with <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> and read from the
/// cursor to the current length, never by modification time: Codex keeps an open rollout's mtime unchanged. A
/// new file restores state from its first line (the <c>session_meta</c>) and its last
/// <see cref="RolloutTailerOptions.HistoryBytes"/> only.</para>
/// <para>Lines are split on the <c>\n</c> byte, which never occurs inside a UTF-8 sequence, so a partly written
/// record or character waits in the cursor until its line ends; a trailing <c>\r</c> is dropped. A line longer
/// than <see cref="RolloutTailerOptions.MaxLineBytes"/> is skipped without being buffered.</para>
/// <para>A file that shrank or was re-created (a new creation time) gets a fresh cursor; records read again at the
/// same path, offset and content are dropped. A deleted file loses its cursor; when the same thread comes back
/// (for example a <c>.jsonl.zst</c> decompressed on resume) it is read as a new file. <c>.jsonl.zst</c> files are
/// never read: they are cold (no record for about 7 days) and need a zstd decoder.</para>
/// </remarks>
public sealed class RolloutTail
{
    private const int ReadBufferBytes = 64 * 1024;
    private const int MaxReplayKeys = 4096;
    private const string RolloutExtension = ".jsonl";

    private readonly RolloutTailerOptions _options;
    private readonly IClock _clock;
    private readonly Dictionary<string, RolloutCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _changed = new();
    private readonly HashSet<ReplayKey> _replayKeys = [];
    private readonly Queue<ReplayKey> _replayOrder = new();
    private DateTimeOffset? _lastDiscoveryUtc;
    private volatile bool _rescanRequested;

    /// <summary>Creates a reader with nothing tracked yet.</summary>
    /// <param name="options">The bounds.</param>
    /// <param name="clock">The clock that dates discovery.</param>
    public RolloutTail(RolloutTailerOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        options.Validate();
        _options = options;
        _clock = clock;
    }

    /// <summary>Gets whether the sessions folder was found at the last poll.</summary>
    public SourceStatus Status { get; private set; } = SourceStatus.Available;

    /// <summary>Gets the counters of skipped and recovered records.</summary>
    public RolloutDiagnostics Diagnostics { get; } = new();

    /// <summary>Gets the number of files with a cursor.</summary>
    public int TrackedFiles => _cursors.Count;

    /// <summary>Notes that a file changed, was created or was renamed; it is read at the next poll.</summary>
    /// <param name="path">The full path from the watcher.</param>
    public void MarkChanged(string path)
    {
        _changed.Enqueue(path);
    }

    /// <summary>Makes the next poll list the day folders again (after a watcher overflow).</summary>
    public void RequestRescan()
    {
        _rescanRequested = true;
    }

    /// <summary>Discovers when due, then reads what every tracked or changed file appended since the last poll.</summary>
    /// <returns>The new observations and usage events.</returns>
    public RolloutPollResult Poll()
    {
        List<ThreadObservation> observations = [];
        List<TokenUsageSnapshot> usages = [];

        if (!Directory.Exists(_options.Root))
        {
            Status = SourceStatus.NotFound;
            return new RolloutPollResult(observations, usages);
        }

        Status = SourceStatus.Available;
        DateTimeOffset now = _clock.UtcNow;

        if (_rescanRequested || _lastDiscoveryUtc is not { } last || now - last >= _options.RediscoverInterval || now < last)
        {
            _rescanRequested = false;
            _lastDiscoveryUtc = now;

            foreach (string path in Discover(now))
            {
                Track(path, now);
            }
        }

        while (_changed.TryDequeue(out string? path))
        {
            if (path.EndsWith(RolloutExtension, StringComparison.OrdinalIgnoreCase)
             && RolloutFileName.TryGetThreadId(path, out _)
             && File.Exists(path))
            {
                Track(path, now);
            }
        }

        foreach (RolloutCursor cursor in _cursors.Values.ToArray())
        {
            Read(cursor, now, observations, usages);
        }

        return new RolloutPollResult(observations, usages);
    }

    private IEnumerable<string> Discover(DateTimeOffset nowUtc)
    {
        DateTime today = nowUtc.ToLocalTime().Date;
        List<FileInfo> files = [];

        // The day folder is local time; tomorrow covers a clock near midnight in another zone.
        for (int day = -1; day <= _options.MaxAgeDays; day++)
        {
            DateTime date = today.AddDays(-day);
            string folder = Path.Combine(_options.Root, date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd"));

            if (Directory.Exists(folder))
            {
                files.AddRange(new DirectoryInfo(folder).EnumerateFiles("rollout-*" + RolloutExtension)
                                                        .Where(f => f.Name.EndsWith(RolloutExtension, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return files.OrderByDescending(f => f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc)
                    .Take(_options.MaxFiles)
                    .Select(f => f.FullName);
    }

    private void Track(string path, DateTimeOffset nowUtc)
    {
        string fullPath = Path.GetFullPath(path);

        if (_cursors.ContainsKey(fullPath))
        {
            return;
        }

        if (_cursors.Count >= _options.MaxFiles)
        {
            RolloutCursor stalest = _cursors.Values.MinBy(c => c.LastGrowthUtc)!;
            _cursors.Remove(stalest.Path);
        }

        _cursors[fullPath] = new RolloutCursor(fullPath, nowUtc);
    }

    private void Read(RolloutCursor cursor,
                      DateTimeOffset nowUtc,
                      List<ThreadObservation> observations,
                      List<TokenUsageSnapshot> usages)
    {
        FileInfo file = new(cursor.Path);

        if (!file.Exists)
        {
            _cursors.Remove(cursor.Path);
            return;
        }

        if (cursor.IsOpened && (file.Length < cursor.Offset || file.CreationTimeUtc != cursor.CreationTimeUtc))
        {
            Diagnostics.Increment(DiagnosticCodes.FileReset);
            cursor = new RolloutCursor(cursor.Path, nowUtc);
            _cursors[cursor.Path] = cursor;
        }

        if (cursor.IsOpened && file.Length == cursor.Offset)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);

        try
        {
            using FileStream stream = new(cursor.Path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

            if (!cursor.IsOpened)
            {
                Open(cursor, stream, file.CreationTimeUtc, buffer, observations, usages);
            }

            ReadAppended(cursor, stream, buffer, observations, usages);
            cursor.LastGrowthUtc = nowUtc;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Increment(DiagnosticCodes.ReadFailed);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads the first line (the <c>session_meta</c>), then places the cursor at the start of the history window.</summary>
    private void Open(RolloutCursor cursor,
                      FileStream stream,
                      DateTime creationTimeUtc,
                      byte[] buffer,
                      List<ThreadObservation> observations,
                      List<TokenUsageSnapshot> usages)
    {
        long length = stream.Length;
        long headEnd = 0;
        byte[] head = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, _options.MaxLineBytes));

        try
        {
            int limit = (int)Math.Min(length, _options.MaxLineBytes);
            int filled = 0;
            int newline = -1;

            while (newline < 0 && filled < limit)
            {
                int read = stream.Read(head, filled, Math.Min(buffer.Length, limit - filled));

                if (read == 0)
                {
                    break;
                }

                int found = head.AsSpan(filled, read).IndexOf((byte)'\n');
                newline = found < 0 ? -1 : filled + found;
                filled += read;
            }

            // Without a complete first line (still being written, or too long) the normal path below reads it.
            if (newline >= 0)
            {
                Handle(cursor, TrimCarriageReturn(head.AsSpan(0, newline)), 0, observations, usages);
                headEnd = newline + 1;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(head);
        }

        long start = Math.Max(headEnd, length - _options.HistoryBytes);
        cursor.IsOpened = true;
        cursor.CreationTimeUtc = creationTimeUtc;

        if (start > headEnd)
        {
            // Resynchronize on the first line boundary at or after the window start: from the byte before it,
            // skip through the next '\n'. When that byte is itself '\n', nothing but it is skipped.
            cursor.Offset = start - 1;
            cursor.SkipToNextLine();
        }
        else
        {
            cursor.Offset = headEnd;
        }
    }

    private void ReadAppended(RolloutCursor cursor,
                              FileStream stream,
                              byte[] buffer,
                              List<ThreadObservation> observations,
                              List<TokenUsageSnapshot> usages)
    {
        stream.Seek(cursor.Offset, SeekOrigin.Begin);
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            long chunkStart = cursor.Offset;
            int position = 0;

            while (position < read)
            {
                int newline = buffer.AsSpan(position, read - position).IndexOf((byte)'\n');

                if (newline < 0)
                {
                    if (!cursor.Hold(buffer.AsSpan(position, read - position), chunkStart + position, _options.MaxLineBytes))
                    {
                        Diagnostics.Increment(DiagnosticCodes.LineTooLong);
                    }

                    break;
                }

                ReadOnlySpan<byte> piece = buffer.AsSpan(position, newline);

                if (cursor.TryCompleteLine(piece, chunkStart + position, _options.MaxLineBytes,
                                           out ReadOnlySpan<byte> line, out long lineStart, out bool tooLong))
                {
                    Handle(cursor, TrimCarriageReturn(line), lineStart, observations, usages);
                }
                else if (tooLong)
                {
                    Diagnostics.Increment(DiagnosticCodes.LineTooLong);
                }

                cursor.ClearPending();
                position += newline + 1;
            }

            cursor.Offset = chunkStart + read;
        }
    }

    private void Handle(RolloutCursor cursor,
                        ReadOnlySpan<byte> line,
                        long lineStart,
                        List<ThreadObservation> observations,
                        List<TokenUsageSnapshot> usages)
    {
        if (line.IsEmpty)
        {
            return;
        }

        ParsedLine parsed = cursor.Parser.Parse(line, lineStart);

        if (parsed.Diagnostic is { } code)
        {
            Diagnostics.Increment(code);
        }

        if (parsed.Observation is { } observation)
        {
            observations.Add(observation);
        }

        if (parsed.Usage is { } usage)
        {
            HashCode hash = new();
            hash.AddBytes(line);

            if (Remember(new ReplayKey(cursor.Path.ToUpperInvariant(), lineStart, hash.ToHashCode())))
            {
                usages.Add(usage);
            }
            else
            {
                Diagnostics.Increment(DiagnosticCodes.ReplayedRecord);
            }
        }
    }

    private bool Remember(ReplayKey key)
    {
        if (!_replayKeys.Add(key))
        {
            return false;
        }

        _replayOrder.Enqueue(key);

        if (_replayOrder.Count > MaxReplayKeys)
        {
            _replayKeys.Remove(_replayOrder.Dequeue());
        }

        return true;
    }

    private static ReadOnlySpan<byte> TrimCarriageReturn(ReadOnlySpan<byte> line)
    {
        return line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;
    }

    private readonly record struct ReplayKey(string Path, long ByteStart, int Hash);
}
