using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;

namespace CodexSessionPulse.App.Ipc;

/// <summary>
/// The overlay end of the hook pipe: accepts any number of short connections, reads one newline-terminated
/// <see cref="HookMessage"/> from each, answers with one acknowledgement byte and queues the message for one reader, so the
/// overlay applies hook events strictly one after another. The acknowledgement keeps the hook connected until its message is
/// read: a client that wrote and closed before the server's wait began would otherwise lose it. It runs on the thread pool
/// and is created before WPF starts, so a hook that started the overlay finds the pipe early.
/// </summary>
internal sealed class HookPipeServer : IDisposable
{
    private const int QueueCapacity = 256;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly Action<Exception> _onError;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<HookMessage> _messages = Channel.CreateBounded<HookMessage>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private long _rejected;

    private HookPipeServer(string pipeName, Action<Exception> onError)
    {
        _pipeName = pipeName;
        _onError = onError;
    }

    /// <summary>Gets the received messages, in arrival order.</summary>
    public ChannelReader<HookMessage> Messages => _messages.Reader;

    /// <summary>Gets the number of connections whose data was not a valid message.</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>Creates the first pipe instance and starts accepting.</summary>
    /// <param name="pipeName">The pipe name.</param>
    /// <param name="onError">Called from the thread pool when accepting fails for good; hooks then fall back to JSONL.</param>
    /// <returns>The server, or <see langword="null"/> when another process already owns the pipe.</returns>
    public static HookPipeServer? TryStart(string pipeName, Action<Exception> onError)
    {
        NamedPipeServerStream first;

        try
        {
            first = Create(pipeName, PipeOptions.FirstPipeInstance);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        HookPipeServer server = new(pipeName, onError);
        _ = Task.Run(() => server.AcceptLoggedAsync(first));

        return server;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _messages.Writer.TryComplete();
    }

    private static NamedPipeServerStream Create(string pipeName, PipeOptions options)
    {
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                         PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | options,
                                         HookMessage.MaxBytes, 1);
    }

    private async Task AcceptLoggedAsync(NamedPipeServerStream first)
    {
        try
        {
            await AcceptAsync(first).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _onError(exception);
        }
    }

    private async Task AcceptAsync(NamedPipeServerStream pipe)
    {
        CancellationToken token = _stop.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // A client that connected and gave up before the wait began; it got no acknowledgement and knows it.
                await pipe.DisposeAsync().ConfigureAwait(false);
                pipe = Create(_pipeName, PipeOptions.None);

                continue;
            }

            NamedPipeServerStream connected = pipe;

            // The next instance exists before this one is served, so a hook connecting meanwhile finds a listener.
            pipe = Create(_pipeName, PipeOptions.None);
            _ = ServeAsync(connected, token);
        }

        await pipe.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe.ConfigureAwait(false))
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ReadTimeout);
            byte[] buffer = new byte[HookMessage.MaxBytes + 1];
            int length = 0;
            int end;

            try
            {
                int read;

                while ((end = buffer.AsSpan(0, length).IndexOf((byte)'\n')) < 0 && length < buffer.Length
                       && (read = await pipe.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    length += read;
                }

                bool accepted = end >= 0 && HookMessage.TryParse(buffer.AsSpan(0, end), out HookMessage message)
                                && _messages.Writer.TryWrite(message);

                if (!accepted)
                {
                    Interlocked.Increment(ref _rejected);
                }

                await pipe.WriteAsync(new[] { accepted ? HookMessage.Accepted : HookMessage.Refused }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
                // A client that gave up before its acknowledgement; whatever it sent was handled above or rejected.
                Interlocked.Increment(ref _rejected);
            }
        }
    }
}
