using System.IO;
using System.Text.Json;

namespace CodexSessionPulse.App.Hooks;

/// <summary>The fields of a hook payload that the hook path reads; <c>cwd</c> and <c>prompt</c> are never among them.</summary>
/// <param name="HookEventName">The lifecycle event name, when present.</param>
/// <param name="SessionId">The session id, when present; a fork or subagent shares it, so it never names the thread.</param>
/// <param name="TranscriptPath">The rollout path, when present; the thread id comes from its file name.</param>
/// <param name="Source">The <c>SessionStart</c> source (startup, resume, clear, compact, fork), when present.</param>
internal sealed record HookPayload(string? HookEventName, string? SessionId, string? TranscriptPath, string? Source);

/// <summary>
/// Reads the hook payload from stdin incrementally with <see cref="Utf8JsonReader"/> (spec §4.4 item 1c): at most
/// <see cref="MaxPayloadBytes"/> bytes, stopping at the end of the root value. Only the four string fields of the root
/// object are decoded; every other value, the prompt included, is passed over as raw bytes and never becomes a string.
/// Truncated or invalid JSON yields the fields found before the fault.
/// </summary>
internal static class HookPayloadReader
{
    /// <summary>The maximum number of stdin bytes that are read.</summary>
    internal const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>The longest field value that is decoded; a longer one is ignored.</summary>
    private const int MaxFieldBytes = 4096;

    private const int InitialBufferBytes = 16 * 1024;

    /// <summary>Reads the payload.</summary>
    /// <param name="stdin">The stream holding the payload.</param>
    /// <returns>The fields found; all <see langword="null"/> for input that is not a JSON object.</returns>
    public static HookPayload Read(Stream stdin)
    {
        byte[] buffer = new byte[InitialBufferBytes];
        int buffered = 0;
        long total = 0;
        Parser parser = new();

        while (!parser.Done)
        {
            if (buffered == buffer.Length)
            {
                // One token, such as a long prompt, fills the buffer: grow it, still within the cap.
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, MaxPayloadBytes));
            }

            int read = total < MaxPayloadBytes
                ? stdin.Read(buffer, buffered, (int)Math.Min(buffer.Length - buffered, MaxPayloadBytes - total))
                : 0;
            total += read;
            buffered += read;

            int consumed = parser.Feed(buffer.AsSpan(0, buffered), isFinalBlock: read == 0);
            Buffer.BlockCopy(buffer, consumed, buffer, 0, buffered - consumed);
            buffered -= consumed;

            if (read == 0)
            {
                break;
            }
        }

        return parser.ToPayload();
    }

    /// <summary>The reader state carried from one chunk to the next.</summary>
    private sealed class Parser
    {
        private JsonReaderState _state;
        private Field _pending;
        private string? _hookEventName;
        private string? _sessionId;
        private string? _transcriptPath;
        private string? _source;

        private enum Field
        {
            None,
            HookEventName,
            SessionId,
            TranscriptPath,
            Source,
        }

        /// <summary>Gets whether the root value is complete or the input is invalid.</summary>
        public bool Done { get; private set; }

        /// <summary>Reads the complete tokens of a chunk.</summary>
        /// <param name="data">The unconsumed bytes.</param>
        /// <param name="isFinalBlock">Whether no more bytes will follow.</param>
        /// <returns>The number of bytes consumed; the rest starts an incomplete token.</returns>
        public int Feed(ReadOnlySpan<byte> data, bool isFinalBlock)
        {
            Utf8JsonReader reader = new(data, isFinalBlock, _state);

            try
            {
                while (!Done && reader.Read())
                {
                    Take(ref reader);
                }
            }
            catch (JsonException)
            {
                // Invalid or truncated JSON: keep what was found before the fault.
                Done = true;
            }

            _state = reader.CurrentState;

            return (int)reader.BytesConsumed;
        }

        /// <summary>Gets the fields found.</summary>
        /// <returns>The payload.</returns>
        public HookPayload ToPayload() => new(_hookEventName, _sessionId, _transcriptPath, _source);

        private void Take(ref Utf8JsonReader reader)
        {
            if (reader.CurrentDepth == 0 && reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
            {
                // The end of the root value; anything after it is ignored.
                Done = true;

                return;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _pending = reader.CurrentDepth == 1 ? Match(ref reader) : Field.None;

                return;
            }

            Field field = _pending;
            _pending = Field.None;

            if (field == Field.None || reader.TokenType != JsonTokenType.String
                || (reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) > MaxFieldBytes)
            {
                return;
            }

            string value = reader.GetString()!;

            switch (field)
            {
                case Field.HookEventName:
                    _hookEventName = value;
                    break;
                case Field.SessionId:
                    _sessionId = value;
                    break;
                case Field.TranscriptPath:
                    _transcriptPath = value;
                    break;
                case Field.Source:
                    _source = value;
                    break;
            }
        }

        private static Field Match(ref Utf8JsonReader reader)
        {
            return reader.ValueTextEquals("hook_event_name"u8) ? Field.HookEventName
                 : reader.ValueTextEquals("session_id"u8) ? Field.SessionId
                 : reader.ValueTextEquals("transcript_path"u8) ? Field.TranscriptPath
                 : reader.ValueTextEquals("source"u8) ? Field.Source
                 : Field.None;
        }
    }
}
