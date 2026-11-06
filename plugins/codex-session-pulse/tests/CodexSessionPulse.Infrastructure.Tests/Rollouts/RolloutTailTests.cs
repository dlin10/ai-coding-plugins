using System.Globalization;
using System.Text;
using CodexSessionPulse.Core;
using CodexSessionPulse.Infrastructure.Rollouts;
using Xunit;
using static CodexSessionPulse.Infrastructure.Tests.Rollouts.RolloutTestRoot;

namespace CodexSessionPulse.Infrastructure.Tests.Rollouts;

/// <summary>How bytes arrive: appends, partial records, line endings, truncation, segments, .zst, bounds (spec §5.2, §11.2).</summary>
public sealed class RolloutTailTests : IDisposable
{
    private readonly RolloutTestRoot _root = new();

    /// <inheritdoc/>
    public void Dispose()
    {
        _root.Dispose();
    }

    /// <summary>A writer keeps the file open and appends; the reader never blocks it and reads only the new bytes.</summary>
    [Fact]
    public void Poll_AppendsToAnOpenFile_ReadsOnlyNewRecordsWithoutBlockingTheWriter()
    {
        string path = _root.RolloutPath(ThreadA);
        using FileStream writer = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
        Write(writer, Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n");
        RolloutTail tail = _root.CreateTail();

        RolloutPollResult first = tail.Poll();
        Write(writer, TokenCount(2, 2, 11000, 10000) + "\n");
        RolloutPollResult second = tail.Poll();
        RolloutPollResult third = tail.Poll();

        Assert.Equal([10000L], first.Usages.Select(u => u.InputTokens));
        Assert.Equal([11000L], second.Usages.Select(u => u.InputTokens));
        Assert.Empty(third.Usages);
        Assert.Single(first.Observations);
        Assert.Empty(second.Observations);
    }

    /// <summary>A record cut inside a multi-byte UTF-8 character waits for the rest of its line.</summary>
    [Fact]
    public void Poll_PartialUtf8Record_IsReadWhenComplete()
    {
        string path = _root.RolloutPath(ThreadA);
        File.WriteAllText(path, Meta(ThreadA) + "\n");
        byte[] record = Encoding.UTF8.GetBytes(TokenCount(1, 1, 12345, 12000, extra: ",\"note\":\"кэш\"") + "\n");
        int cut = Array.IndexOf(record, (byte)0xD0) + 1;
        RolloutTail tail = _root.CreateTail();
        tail.Poll();

        AppendBytes(path, record.AsSpan(0, cut));
        RolloutPollResult partial = tail.Poll();
        AppendBytes(path, record.AsSpan(cut));
        RolloutPollResult complete = tail.Poll();

        Assert.Empty(partial.Usages);
        Assert.Equal(12345, Assert.Single(complete.Usages).InputTokens);
        Assert.Equal(0, tail.Diagnostics.Count(DiagnosticCodes.ParseFailed));
    }

    /// <summary>CRLF and LF lines read the same, including a CRLF split between two appends.</summary>
    [Fact]
    public void Poll_CrlfAndLf_ReadTheSame()
    {
        string path = _root.RolloutPath(ThreadA);
        File.WriteAllText(path, Meta(ThreadA) + "\r\n" + TokenCount(1, 1, 10000, 9000) + "\r\n" + TokenCount(2, 2, 11000, 10000) + "\r");
        RolloutTail tail = _root.CreateTail();

        RolloutPollResult first = tail.Poll();
        Append(path, "\n" + TokenCount(3, 3, 12000, 11000) + "\n");
        RolloutPollResult second = tail.Poll();

        Assert.Equal([10000L], first.Usages.Select(u => u.InputTokens));
        Assert.Equal([11000L, 12000L], second.Usages.Select(u => u.InputTokens));
        Assert.Equal(0, tail.Diagnostics.Count(DiagnosticCodes.ParseFailed));
    }

    /// <summary>A truncated file is read again from its start; records already emitted at the same offset are not repeated.</summary>
    [Fact]
    public void Poll_Truncation_RebuildsTheCursorWithoutRepeats()
    {
        string path = _root.RolloutPath(ThreadA);
        string original = Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n" + TokenCount(2, 2, 11000, 10000) + "\n";
        File.WriteAllText(path, original);
        RolloutTail tail = _root.CreateTail();
        tail.Poll();

        File.WriteAllText(path, Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n");
        RolloutPollResult afterTruncate = tail.Poll();
        Append(path, TokenCount(5, 5, 15000, 14000) + "\n");
        RolloutPollResult afterAppend = tail.Poll();

        Assert.Empty(afterTruncate.Usages);
        Assert.Equal([15000L], afterAppend.Usages.Select(u => u.InputTokens));
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.FileReset));
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.ReplayedRecord));
    }

    /// <summary>A new _rollout-id segment in the next day folder continues the same thread.</summary>
    [Fact]
    public void Poll_NewSegment_ContinuesTheThread()
    {
        string first = _root.RolloutPath(ThreadA, daysAgo: 1);
        File.WriteAllText(first, Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n");
        RolloutTail tail = _root.CreateTail();
        tail.Poll();

        string segment = _root.RolloutPath(ThreadA, RolloutR);
        string historyBase = $",\"history_base\":{{\"thread_id\":\"{ThreadA}\",\"end_ordinal_exclusive\":2,\"end_byte_offset\":10}}";
        File.WriteAllText(segment, Meta(ThreadA, 30, historyBase) + "\n" + TokenCount(3, 40, 20000, 19000) + "\n");
        tail.MarkChanged(segment);
        RolloutPollResult result = tail.Poll();

        TokenUsageSnapshot usage = Assert.Single(result.Usages);
        Assert.Equal(ThreadA, usage.ThreadId);
        Assert.Equal(segment, usage.SourcePath);
        Assert.Equal(ThreadKind.Main, usage.Kind);
        Assert.Equal(2, tail.TrackedFiles);
    }

    /// <summary>.jsonl → .jsonl.zst (delete + create) and back on resume: no error, no repeated request, the new one read.</summary>
    [Fact]
    public void Poll_CompressedAndRestored_ReadsOnlyNewRecords()
    {
        string path = _root.RolloutPath(ThreadA);
        string content = Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n" + TokenCount(2, 2, 11000, 10000) + "\n";
        File.WriteAllText(path, content);
        RolloutTail tail = _root.CreateTail();
        tail.Poll();

        File.Delete(path);
        File.WriteAllBytes(path + ".zst", [0x28, 0xB5, 0x2F, 0xFD]);
        tail.MarkChanged(path + ".zst");
        RolloutPollResult compressed = tail.Poll();

        File.Delete(path + ".zst");
        File.WriteAllText(path, content + TokenCount(3, 3, 12000, 11000) + "\n");
        tail.MarkChanged(path);
        RolloutPollResult restored = tail.Poll();

        Assert.Empty(compressed.Usages);
        Assert.Equal([12000L], restored.Usages.Select(u => u.InputTokens));
        Assert.Equal(0, tail.Diagnostics.Count(DiagnosticCodes.ReadFailed));
    }

    /// <summary>A large file is not read whole: the session_meta at the head and only the last window are read.</summary>
    [Fact]
    public void Poll_LargeFile_ReadsHeadAndBoundedHistoryOnly()
    {
        string path = _root.RolloutPath(ThreadA);
        StringBuilder content = new(Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n");

        for (int i = 0; i < 200; i++)
        {
            content.Append(Filler(2 + i, 4000)).Append('\n');
        }

        content.Append(TokenCount(300, 30, 50000, 49000)).Append('\n');
        File.WriteAllText(path, content.ToString());
        RolloutTail tail = _root.CreateTail(o => o with { HistoryBytes = 64 * 1024 });

        RolloutPollResult result = tail.Poll();

        Assert.Equal([50000L], result.Usages.Select(u => u.InputTokens));
        Assert.Equal(ThreadA, Assert.Single(result.Observations).ThreadId);
        Assert.Equal(ThreadKind.Main, result.Usages[0].Kind);
    }

    /// <summary>A line over the limit is skipped without being buffered, and the next record is still read.</summary>
    [Fact]
    public void Poll_LineOverTheLimit_IsSkipped()
    {
        string path = _root.RolloutPath(ThreadA);
        File.WriteAllText(path, Meta(ThreadA) + "\n" + Filler(1, 10_000) + "\n" + TokenCount(2, 2, 11000, 10000) + "\n");
        RolloutTail tail = _root.CreateTail(o => o with { MaxLineBytes = 4096 });

        RolloutPollResult result = tail.Poll();

        Assert.Equal(11000, Assert.Single(result.Usages).InputTokens);
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.LineTooLong));
    }

    /// <summary>The same record delivered twice through re-discovery is emitted once.</summary>
    [Fact]
    public void Poll_SameLineDeliveredTwice_IsEmittedOnce()
    {
        string path = _root.RolloutPath(ThreadA);
        File.WriteAllText(path, Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n");
        RolloutTail tail = _root.CreateTail(o => o with { MaxFiles = 1 });
        tail.Poll();

        string other = _root.RolloutPath(ThreadB);
        File.WriteAllText(other, Meta(ThreadB) + "\n");
        tail.MarkChanged(other);
        tail.Poll();
        tail.MarkChanged(path);
        RolloutPollResult again = tail.Poll();

        Assert.Empty(again.Usages);
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.ReplayedRecord));
    }

    /// <summary>Discovery is bounded by file count and by age.</summary>
    [Fact]
    public void Poll_Discovery_IsBounded()
    {
        File.WriteAllText(_root.RolloutPath(ThreadA), Meta(ThreadA) + "\n");
        File.WriteAllText(_root.RolloutPath(ThreadB), Meta(ThreadB) + "\n");
        File.WriteAllText(_root.RolloutPath("00000000-0000-7000-8000-0000000002c1"), Meta("00000000-0000-7000-8000-0000000002c1") + "\n");
        File.WriteAllText(_root.RolloutPath("00000000-0000-7000-8000-0000000002d1", daysAgo: 9), Meta("00000000-0000-7000-8000-0000000002d1") + "\n");

        RolloutTail limited = _root.CreateTail(o => o with { MaxFiles = 2 });
        RolloutTail wide = _root.CreateTail();
        limited.Poll();
        RolloutPollResult all = wide.Poll();

        Assert.Equal(2, limited.TrackedFiles);
        Assert.Equal(3, wide.TrackedFiles);
        Assert.DoesNotContain(all.Observations, o => o.ThreadId == "00000000-0000-7000-8000-0000000002d1");
    }

    /// <summary>A missing root is NotFound and recovers once Codex creates it.</summary>
    [Fact]
    public void Poll_MissingRoot_IsNotFoundUntilCreated()
    {
        Directory.Delete(_root.Root, recursive: true);
        RolloutTail tail = _root.CreateTail();

        tail.Poll();
        SourceStatus missing = tail.Status;
        File.WriteAllText(_root.RolloutPath(ThreadA), Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n");
        tail.RequestRescan();
        RolloutPollResult result = tail.Poll();

        Assert.Equal(SourceStatus.NotFound, missing);
        Assert.Equal(SourceStatus.Available, tail.Status);
        Assert.Single(result.Usages);
    }

    /// <summary>A broken line is counted and does not stop the reader.</summary>
    [Fact]
    public void Poll_BrokenLine_IsCountedAndSkipped()
    {
        string path = _root.RolloutPath(ThreadA);
        File.WriteAllText(path, Meta(ThreadA) + "\n{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\n" + TokenCount(2, 2, 11000, 10000) + "\n");
        RolloutTail tail = _root.CreateTail();

        RolloutPollResult result = tail.Poll();

        Assert.Single(result.Usages);
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.ParseFailed));
    }

    /// <summary>The bounds follow the spec: polling outside 1–3 seconds is refused.</summary>
    [Fact]
    public void Options_PollingOutsideOneToThreeSeconds_IsRefused()
    {
        RolloutTailerOptions options = RolloutTailerOptions.ForRoot(_root.Root) with { PollInterval = TimeSpan.FromSeconds(5) };

        Assert.Throws<ArgumentOutOfRangeException>(() => new RolloutTail(options, _root.Clock));
    }

    /// <summary>CODEX_HOME moves the sessions folder; without it the profile folder is used.</summary>
    [Fact]
    public void DefaultRoot_HonoursCodexHome()
    {
        string custom = RolloutTailerOptions.DefaultRoot(name => name == "CODEX_HOME" ? @"D:\codex" : null);
        string profile = RolloutTailerOptions.DefaultRoot(_ => null);

        Assert.Equal(@"D:\codex\sessions", custom);
        Assert.EndsWith(Path.Combine(".codex", "sessions"), profile);
    }

    /// <summary>
    /// A file replaced in place with the old creation time (NTFS tunneling) and no shorter length is recognized by
    /// its head and read anew.
    /// </summary>
    [Fact]
    public void Poll_ReplacedWithSameCreationTime_IsReadAnew()
    {
        string path = _root.RolloutPath(ThreadA);
        File.WriteAllText(path, Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n");
        RolloutTail tail = _root.CreateTail();
        tail.Poll();
        DateTime created = File.GetCreationTimeUtc(path);

        File.Delete(path);
        File.WriteAllText(path, Meta(ThreadB) + "\n" + TokenCount(1, 1, 30000, 29000) + "\n" + TokenCount(2, 2, 31000, 30000) + "\n");
        File.SetCreationTimeUtc(path, created);
        RolloutPollResult result = tail.Poll();

        Assert.Equal([30000L, 31000L], result.Usages.Select(u => u.InputTokens));
        Assert.All(result.Usages, u => Assert.Equal(ThreadB, u.ThreadId));
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.FileReset));
    }

    /// <summary>When the file limit is reached, discovery does not push out a file that the watcher reported active.</summary>
    [Fact]
    public void Poll_FullAndRediscovered_KeepsTheActiveFile()
    {
        string first = _root.RolloutPath(ThreadA);
        File.WriteAllText(first, Meta(ThreadA) + "\n");
        RolloutTail tail = _root.CreateTail(o => o with { MaxFiles = 1 });
        tail.Poll();

        string active = _root.RolloutPath(ThreadB);
        File.WriteAllText(active, Meta(ThreadB) + "\n");
        tail.MarkChanged(active);
        tail.Poll();
        tail.RequestRescan();
        tail.Poll();
        Append(active, TokenCount(1, 1, 20000, 19000) + "\n");
        RolloutPollResult result = tail.Poll();

        Assert.Equal(1, tail.TrackedFiles);
        Assert.Equal(ThreadB, Assert.Single(result.Usages).ThreadId);
    }

    /// <summary>Day folders are Gregorian whatever the current culture's calendar is.</summary>
    [Fact]
    public void Poll_ThaiCulture_FindsGregorianDayFolders()
    {
        File.WriteAllText(_root.RolloutPath(ThreadA), Meta(ThreadA) + "\n" + TokenCount(1, 1, 10000, 9000) + "\n");
        CultureInfo previous = CultureInfo.CurrentCulture;
        RolloutPollResult result;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            result = _root.CreateTail().Poll();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Single(result.Usages);
    }

    /// <summary>The thread id comes from plain, segment and compressed names; other names are refused.</summary>
    /// <param name="path">The path or name.</param>
    /// <param name="expected">The thread id, or <see langword="null"/> for a name that is not a rollout.</param>
    [Theory]
    [InlineData("rollout-2026-11-06T23-50-33-00000000-0000-7000-8000-000000000001.jsonl", "00000000-0000-7000-8000-000000000001")]
    [InlineData(@"C:\s\rollout-2026-11-07T00-41-23-00000000-0000-7000-8000-000000000001_00000000-0000-7000-8000-000000000101.jsonl", "00000000-0000-7000-8000-000000000001")]
    [InlineData("rollout-2026-11-06T23-50-33-00000000-0000-7000-8000-000000000001.jsonl.zst", "00000000-0000-7000-8000-000000000001")]
    [InlineData("history.jsonl", null)]
    [InlineData(null, null)]
    public void RolloutFileName_ReadsTheThreadId(string? path, string? expected)
    {
        bool found = RolloutFileName.TryGetThreadId(path, out string threadId);

        Assert.Equal(expected is not null, found);
        Assert.Equal(expected ?? string.Empty, threadId);
    }

    private static void Write(FileStream writer, string text)
    {
        writer.Write(Encoding.UTF8.GetBytes(text));
        writer.Flush();
    }

    private static void AppendBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes);
    }
}
