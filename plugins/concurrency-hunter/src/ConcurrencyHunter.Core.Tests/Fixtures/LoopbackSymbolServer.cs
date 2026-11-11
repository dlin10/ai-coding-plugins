using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

internal sealed record SymbolReply(int Status = 200, byte[]? Bytes = null, TimeSpan Delay = default,
                                   bool StallBody = false, string? SetCookie = null, string? Redirect = null, string? RetryAfter = null);
internal sealed record SymbolRequest(string Path, IReadOnlyDictionary<string, string> Headers);

internal sealed class LoopbackSymbolServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentBag<Task> _responses = [];
    private readonly Task _accepting;
    internal ConcurrentDictionary<string, SymbolReply> Replies { get; } = new(StringComparer.Ordinal);
    internal ConcurrentQueue<SymbolRequest> Requests { get; } = new();
    internal Action<SymbolRequest>? RequestReceived { get; set; }
    internal Action<SymbolRequest>? BodyStalled { get; set; }
    internal Uri Address { get; }

    internal LoopbackSymbolServer(bool localhost = false)
    {
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        Address = new Uri($"http://{(localhost ? "localhost" : "127.0.0.1")}:{((IPEndPoint)port.LocalEndpoint).Port}/");
        port.Stop();
        _listener.Prefixes.Add(Address.AbsoluteUri);
        _listener.Start();
        _accepting = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_stopping.Token);
                _responses.Add(RespondAsync(context));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.RawUrl!;
            var request = new SymbolRequest(path, context.Request.Headers.AllKeys.Where(key => key is not null)
                .ToDictionary(key => key!, key => context.Request.Headers[key!]!, StringComparer.OrdinalIgnoreCase));
            Requests.Enqueue(request);
            RequestReceived?.Invoke(request);
            var reply = Replies.GetValueOrDefault(path) ?? new SymbolReply(404);
            if (reply.Delay != default)
                await Task.Delay(reply.Delay, _stopping.Token);
            context.Response.StatusCode = reply.Status;
            if (reply.SetCookie is not null)
                context.Response.Headers.Add("Set-Cookie", reply.SetCookie);
            if (reply.Redirect is not null)
                context.Response.RedirectLocation = reply.Redirect;
            if (reply.RetryAfter is not null)
                context.Response.Headers.Add("Retry-After", reply.RetryAfter);
            var bytes = reply.Bytes ?? [];
            context.Response.ContentLength64 = bytes.Length + (reply.StallBody ? 100 : 0);
            await context.Response.OutputStream.WriteAsync(bytes, _stopping.Token);
            await context.Response.OutputStream.FlushAsync(_stopping.Token);
            if (reply.StallBody)
            {
                BodyStalled?.Invoke(request);
                await Task.Delay(Timeout.InfiniteTimeSpan, _stopping.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException) { }
        finally
        {
            try
            {
                if (_stopping.IsCancellationRequested) context.Response.Abort();
                else context.Response.Close();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { }
        }
    }

    // This transport lets the HTTPS policy see an HTTPS initial URI while the fixture stays on plain loopback HTTP.
    internal HttpMessageHandler HttpsHandler() => new LoopbackTransport(Address);

    private sealed class LoopbackTransport(Uri address) : HttpMessageHandler
    {
        private readonly HttpClient _client = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null
        });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, new Uri(address, request.RequestUri!.PathAndQuery));
            foreach (var header in request.Headers)
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return await _client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _client.Dispose();
            base.Dispose(disposing);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        await _accepting;
        await Task.WhenAll(_responses);
        _listener.Close();
        _stopping.Dispose();
    }
}
