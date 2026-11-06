using System.Text;
using CodexSessionPulse.Core;
using CodexSessionPulse.Infrastructure.Rollouts;

namespace CodexSessionPulse.Infrastructure.Tests.Rollouts;

/// <summary>A settable clock.</summary>
internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

/// <summary>A throw-away sessions folder with a day folder for the fake clock's date, and record builders.</summary>
internal sealed class RolloutTestRoot : IDisposable
{
    public const string ThreadA = "00000000-0000-7000-8000-0000000002a1";
    public const string ThreadB = "00000000-0000-7000-8000-0000000002b1";
    public const string RolloutR = "00000000-0000-7000-8000-0000000002f1";

    public RolloutTestRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "csp-tests", Guid.NewGuid().ToString("N"));
        Clock = new FakeClock(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(DayFolder(0));
    }

    public string Root { get; }

    public FakeClock Clock { get; }

    public static string FixturePath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "rollouts", name);
    }

    public string DayFolder(int daysAgo)
    {
        DateTime date = Clock.UtcNow.ToLocalTime().Date.AddDays(-daysAgo);
        return Path.Combine(Root, date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd"));
    }

    public string RolloutPath(string threadId, string? rolloutId = null, int daysAgo = 0)
    {
        string suffix = rolloutId is null ? string.Empty : "_" + rolloutId;
        Directory.CreateDirectory(DayFolder(daysAgo));
        return Path.Combine(DayFolder(daysAgo), $"rollout-2026-11-07T10-00-00-{threadId}{suffix}.jsonl");
    }

    public string CopyFixture(string name, int daysAgo = 0)
    {
        string target = Path.Combine(DayFolder(daysAgo), Path.GetFileName(name));
        File.Copy(FixturePath(name), target);
        return target;
    }

    public RolloutTail CreateTail(Func<RolloutTailerOptions, RolloutTailerOptions>? configure = null)
    {
        RolloutTailerOptions options = RolloutTailerOptions.ForRoot(Root);
        return new RolloutTail(configure?.Invoke(options) ?? options, Clock);
    }

    public static string Meta(string threadId, int second = 0, string extra = "")
    {
        return $$$"""{"timestamp":"2026-11-07T10:00:{{{second:00}}}.000Z","type":"session_meta","ordinal":0,"payload":{"session_id":"{{{threadId}}}","id":"{{{threadId}}}","cli_version":"0.167.0-alpha.3.2","source":"vscode","thread_source":"user"{{{extra}}}}}""";
    }

    public static string TokenCount(int ordinal, int second, long input, long cached, string extra = "")
    {
        return $$$"""{"timestamp":"2026-11-07T10:01:{{{second:00}}}.000Z","type":"event_msg","ordinal":{{{ordinal}}},"payload":{"type":"token_count"{{{extra}}},"info":{"total_token_usage":{"input_tokens":{{{input * 10}}},"cached_input_tokens":{{{cached * 10}}},"output_tokens":50,"total_tokens":{{{input * 10 + 50}}}},"last_token_usage":{"input_tokens":{{{input}}},"cached_input_tokens":{{{cached}}},"cache_write_input_tokens":0,"output_tokens":5,"reasoning_output_tokens":0,"total_tokens":{{{input + 5}}}""" + "}}}}";
    }

    public static string Filler(int ordinal, int bytes)
    {
        return $$$"""{"timestamp":"2026-11-07T10:00:30.000Z","type":"response_item","ordinal":{{{ordinal}}},"payload":{"type":"message","text":"{{{new string('x', bytes)}}}"}}""";
    }

    public static void Append(string path, string text)
    {
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A watcher may still hold the folder for a moment; the temp folder is cleaned up later anyway.
        }
    }
}
