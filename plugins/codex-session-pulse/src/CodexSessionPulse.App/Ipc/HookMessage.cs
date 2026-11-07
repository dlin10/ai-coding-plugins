using System.Buffers;
using System.Globalization;
using System.Text.Json;
using CodexSessionPulse.App.Hooks;
using CodexSessionPulse.Infrastructure.Rollouts;

namespace CodexSessionPulse.App.Ipc;

/// <summary>
/// What a hook tells the overlay over the pipe: one small UTF-8 JSON object per connection. It carries no prompt, no
/// <c>cwd</c> and no path, only the event, the thread id taken from <c>transcript_path</c>, the <c>SessionStart</c> source
/// and when the hook started.
/// </summary>
/// <param name="Event">The <c>hook_event_name</c>.</param>
/// <param name="ThreadId">The thread id from the rollout file name, or <see langword="null"/> when <c>transcript_path</c> is null or not a rollout name (an ephemeral suggestion thread).</param>
/// <param name="Source">The <c>SessionStart</c> source, or <see langword="null"/>.</param>
/// <param name="AtUtc">When the hook process started, in UTC.</param>
internal sealed record HookMessage(string Event, string? ThreadId, string? Source, DateTimeOffset AtUtc)
{
    /// <summary>The largest message the overlay accepts, its newline included.</summary>
    public const int MaxBytes = 4096;

    /// <summary>The overlay's answer to a message it queued.</summary>
    public const byte Accepted = 1;

    /// <summary>The overlay's answer to data that is not a valid message.</summary>
    public const byte Refused = 0;

    /// <summary>The longest event or source name that is passed on; longer ones are cut.</summary>
    private const int MaxNameLength = 64;

    /// <summary>Builds the message from a hook payload.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="atUtc">When the hook started.</param>
    /// <returns>The message; an unknown event is named "unknown".</returns>
    public static HookMessage From(HookPayload payload, DateTimeOffset atUtc)
    {
        string? threadId = RolloutFileName.TryGetThreadId(payload.TranscriptPath, out string id) ? id : null;

        return new HookMessage(Cut(payload.HookEventName) ?? "unknown", threadId, Cut(payload.Source), atUtc);
    }

    /// <summary>Serializes the message.</summary>
    /// <returns>The UTF-8 bytes.</returns>
    public byte[] ToBytes()
    {
        ArrayBufferWriter<byte> buffer = new(256);

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", PulseChannel.ProtocolMajor);
            writer.WriteString("event", Event);
            writer.WriteString("thread", ThreadId);
            writer.WriteString("source", Source);
            writer.WriteString("at", AtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Parses a message; anything malformed, of another protocol version or oversized is rejected.</summary>
    /// <param name="data">The received bytes.</param>
    /// <param name="message">The message when it parses.</param>
    /// <returns>Whether the bytes were a valid message.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out HookMessage message)
    {
        message = null!;

        if (data.Length > MaxBytes)
        {
            return false;
        }

        try
        {
            Utf8JsonReader reader = new(data);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            int version = 0;
            string? eventName = null;
            string? threadId = null;
            string? source = null;
            DateTimeOffset? at = null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string name = reader.GetString()!;
                reader.Read();

                switch (name)
                {
                    case "v" when reader.TokenType == JsonTokenType.Number:
                        version = reader.TryGetInt32(out int v) ? v : 0;
                        break;
                    case "event" when reader.TokenType == JsonTokenType.String:
                        eventName = reader.GetString();
                        break;
                    case "thread" when reader.TokenType == JsonTokenType.String:
                        threadId = reader.GetString();
                        break;
                    case "source" when reader.TokenType == JsonTokenType.String:
                        source = reader.GetString();
                        break;
                    case "at" when reader.TokenType == JsonTokenType.String:
                        at = reader.TryGetDateTimeOffset(out DateTimeOffset parsed) ? parsed.ToUniversalTime() : null;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (version != PulseChannel.ProtocolMajor || string.IsNullOrEmpty(eventName) || at is null)
            {
                return false;
            }

            // The thread id becomes a registry key: accept only what a rollout file name can hold.
            if (threadId is not null && !Guid.TryParse(threadId, out _))
            {
                threadId = null;
            }

            message = new HookMessage(Cut(eventName)!, threadId, Cut(source), at.Value);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Cut(string? value) => value is { Length: > MaxNameLength } ? value[..MaxNameLength] : value;
}
