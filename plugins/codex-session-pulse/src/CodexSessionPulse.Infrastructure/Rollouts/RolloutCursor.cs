namespace CodexSessionPulse.Infrastructure.Rollouts;

/// <summary>
/// The read position of one rollout file and its incomplete last line. The pending bytes never exceed the
/// line limit: a longer line switches the cursor to skipping until the next line break.
/// </summary>
internal sealed class RolloutCursor
{
    private byte[] _pending = [];
    private int _pendingLength;
    private long _pendingStart;
    private bool _skipping;

    /// <summary>Creates an unopened cursor.</summary>
    /// <param name="path">The full path.</param>
    /// <param name="nowUtc">The time it starts being tracked.</param>
    public RolloutCursor(string path, DateTimeOffset nowUtc)
    {
        Path = path;
        Parser = new RolloutFileParser(path);
        LastGrowthUtc = nowUtc;
    }

    /// <summary>Gets the full path.</summary>
    public string Path { get; }

    /// <summary>Gets the parser holding this file's ownership and model state.</summary>
    public RolloutFileParser Parser { get; }

    /// <summary>Gets or sets whether the head and history window were read.</summary>
    public bool IsOpened { get; set; }

    /// <summary>Gets or sets the creation time seen when the file was opened; a change means a replaced file.</summary>
    public DateTime CreationTimeUtc { get; set; }

    /// <summary>Gets or sets the offset of the next byte to read.</summary>
    public long Offset { get; set; }

    /// <summary>Gets or sets when the file last grew; the stalest cursor is dropped first.</summary>
    public DateTimeOffset LastGrowthUtc { get; set; }

    /// <summary>Discards bytes up to and including the next line break.</summary>
    public void SkipToNextLine()
    {
        _skipping = true;
        _pendingLength = 0;
    }

    /// <summary>Holds the start of a line whose break has not arrived yet.</summary>
    /// <param name="bytes">The bytes after the last line break of the chunk.</param>
    /// <param name="start">Their offset in the file.</param>
    /// <param name="maxLineBytes">The line limit.</param>
    /// <returns><see langword="false"/> when the line just went over the limit and will be skipped.</returns>
    public bool Hold(ReadOnlySpan<byte> bytes, long start, int maxLineBytes)
    {
        if (_skipping)
        {
            return true;
        }

        if (_pendingLength + bytes.Length > maxLineBytes)
        {
            SkipToNextLine();
            return false;
        }

        Append(bytes, start);
        return true;
    }

    /// <summary>Completes a line with the bytes before its line break.</summary>
    /// <param name="piece">The bytes of the chunk before the line break.</param>
    /// <param name="pieceStart">Their offset in the file.</param>
    /// <param name="maxLineBytes">The line limit.</param>
    /// <param name="line">The complete line, valid until <see cref="ClearPending"/>.</param>
    /// <param name="lineStart">The offset of the line in the file.</param>
    /// <param name="tooLong">Whether the line went over the limit now.</param>
    /// <returns>Whether a line is available; <see langword="false"/> for a skipped one.</returns>
    public bool TryCompleteLine(ReadOnlySpan<byte> piece,
                                long pieceStart,
                                int maxLineBytes,
                                out ReadOnlySpan<byte> line,
                                out long lineStart,
                                out bool tooLong)
    {
        line = default;
        lineStart = 0;
        tooLong = false;

        if (_skipping)
        {
            _skipping = false;
            return false;
        }

        if (_pendingLength + piece.Length > maxLineBytes)
        {
            tooLong = true;
            return false;
        }

        if (_pendingLength == 0)
        {
            line = piece;
            lineStart = pieceStart;
            return true;
        }

        Append(piece, pieceStart);
        line = _pending.AsSpan(0, _pendingLength);
        lineStart = _pendingStart;
        return true;
    }

    /// <summary>Forgets the completed line.</summary>
    public void ClearPending()
    {
        _pendingLength = 0;
    }

    private void Append(ReadOnlySpan<byte> bytes, long start)
    {
        if (_pendingLength == 0)
        {
            _pendingStart = start;
        }

        int needed = _pendingLength + bytes.Length;

        if (needed > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(needed, _pending.Length * 2));
        }

        bytes.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength = needed;
    }
}
