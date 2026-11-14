using System.IO;
using System.IO.Pipes;

namespace CodexSessionPulse.App.Ipc;

/// <summary>Sends one <see cref="HookMessage"/> to the overlay within fixed time limits (spec §4.4 item 2).</summary>
internal static class HookPipeClient
{
    /// <summary>The longest wait for the pipe of a running overlay.</summary>
    public const int ConnectTimeoutMs = 300;

    /// <summary>The longest wait for the write and the overlay's acknowledgement.</summary>
    public const int WriteTimeoutMs = 200;

    /// <summary>Connects, writes the message with its newline and waits for the overlay's acknowledgement byte.</summary>
    /// <param name="pipeName">The overlay's pipe.</param>
    /// <param name="message">The serialized message, without the newline.</param>
    /// <param name="connectTimeoutMs">How long to wait for the pipe; a missing pipe is retried until then.</param>
    /// <returns>Whether the overlay acknowledged the message.</returns>
    public static bool TrySend(string pipeName, byte[] message, int connectTimeoutMs)
    {
        // CurrentUserOnly also checks that the server end belongs to this user.
        using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);

        try
        {
            pipe.Connect(connectTimeoutMs);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using CancellationTokenSource timeout = new(WriteTimeoutMs);

        try
        {
            byte[] framed = new byte[message.Length + 1];
            message.CopyTo(framed, 0);
            framed[^1] = (byte)'\n';
            pipe.WriteAsync(framed, timeout.Token).AsTask().GetAwaiter().GetResult();

            byte[] answer = new byte[1];

            return pipe.ReadAsync(answer, timeout.Token).AsTask().GetAwaiter().GetResult() == 1 && answer[0] == HookMessage.Accepted;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            return false;
        }
    }
}
