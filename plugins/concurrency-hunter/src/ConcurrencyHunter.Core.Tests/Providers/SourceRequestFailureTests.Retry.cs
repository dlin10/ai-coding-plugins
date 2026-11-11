using System.Net;
using System.Net.Http;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed partial class SourceRequestFailureTests
{
    public static IEnumerable<object[]> RetryCases()
    {
        foreach (var stage in new[] { "microsoft", "nuget", "document" })
        foreach (var status in new[] { 429, 503 })
        foreach (var channel in new[] { "response", "exception", "tunnel" })
        foreach (var succeeds in new[] { false, true })
            yield return [stage, status, channel, succeeds];
    }

    [Theory]
    [MemberData(nameof(RetryCases))]
    public async Task Retried_requests_acquire_sources_or_report_the_last_status_without_skipping_the_host(
        string stage, int status, string channel, bool succeeds)
    {
        await using var server = new LoopbackSymbolServer();
        using var first = new SourceLibrary(server.Address, adjacent: stage == "document", documentCount: 1);
        using var second = new SourceLibrary(server.Address, adjacent: stage == "document", documentCount: 1, crlf: true);
        using var handler = new RetryHandler(stage, status, channel, succeeds);
        using var sources = new LibrarySources(false, first.Cache, new Uri(server.Address, "microsoft/"),
            new Uri(server.Address, "nuget/"), httpsOnly: false, timeout: Timeout.InfiniteTimeSpan, handler: handler)
        { RequestTime = new SourceRetryClock() };
        foreach (var library in new[] { first, second })
        {
            library.Serve(server, stage);
            var result = await sources.GetAsync(library.Dll);
            Assert.Equal(succeeds ? LibraryCodeKind.OriginalSources : LibraryCodeKind.DecompiledForRun, result.Kind);
            Assert.Equal(succeeds ? null : LibraryCodeReason.TemporaryFailure, result.Reason);
            if (succeeds)
                Assert.Null(result.Detail);
            else
            {
                // Alternate retryable statuses so exhaustion must retain the last answer's detail.
                Assert.Contains("HTTP " + (status == 429 ? 503 : 429), result.Detail);
                Assert.Contains(server.Address.Host, result.Detail);
            }
        }
        Assert.Equal(8, handler.Attempts);
        Assert.Equal(succeeds ? stage == "document" ? 2 : 4 : 0, server.Requests.Count);
    }

    [Theory]
    [InlineData(429, "absent", 1, 2, 4)]
    [InlineData(503, "absent", 1, 2, 4)]
    [InlineData(429, "seconds", 7, 7, 7)]
    [InlineData(503, "date", 7, 7, 7)]
    [InlineData(429, "seconds-cap", 60, 60, 60)]
    [InlineData(503, "date-cap", 60, 60, 60)]
    [InlineData(429, "past-date", 0, 0, 0)]
    public async Task Retry_after_and_backoff_use_the_instance_clock(int status, string header, int first, int second, int third)
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address, adjacent: true, documentCount: 1);
        library.Serve(server);
        var clock = new SourceRetryClock(autoAdvance: false);
        var attempts = 0;
        server.RequestReceived = request =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            var retryAfter = header switch
            {
                "seconds" => "7",
                "seconds-cap" => "120",
                "date" => clock.GetUtcNow().AddSeconds(7).ToString("R"),
                "date-cap" => clock.GetUtcNow().AddSeconds(120).ToString("R"),
                "past-date" => clock.GetUtcNow().AddSeconds(-7).ToString("R"),
                _ => null
            };
            server.Replies[request.Path] = attempt < 4 ? new(status, RetryAfter: retryAfter)
                : new(Bytes: library.Documents["/src/file0.cs"]);
        };
        using var sources = new LibrarySources(false, library.Cache, httpsOnly: false, timeout: Timeout.InfiniteTimeSpan)
        { RequestTime = clock };
        var read = sources.GetAsync(library.Dll);
        if (first != 0)
            foreach (var seconds in new[] { first, second, third })
            {
                Assert.Equal(TimeSpan.FromSeconds(seconds), await clock.NextDelayAsync());
                Assert.False(read.IsCompleted);
                clock.Advance(TimeSpan.FromSeconds(seconds));
            }
        Assert.Equal(LibraryCodeKind.OriginalSources, (await read.WaitAsync(TimeSpan.FromSeconds(30))).Kind);
        Assert.Equal(4, attempts);
    }

    [Theory]
    [InlineData("microsoft", 429)]
    [InlineData("nuget", 503)]
    [InlineData("document", 429)]
    public async Task Cancellation_during_a_retry_wait_stops_without_another_request(string stage, int status)
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address, adjacent: stage == "document", documentCount: 1);
        using var handler = new RetryHandler(stage, status, "response", succeeds: false);
        var clock = new SourceRetryClock(autoAdvance: false);
        using var sources = new LibrarySources(false, library.Cache, new Uri(server.Address, "microsoft/"),
            new Uri(server.Address, "nuget/"), httpsOnly: false, timeout: Timeout.InfiniteTimeSpan, handler: handler)
        { RequestTime = clock };
        using var cancellation = new CancellationTokenSource();
        var read = sources.GetAsync(library.Dll, cancellation.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextDelayAsync());
        cancellation.Cancel();
        var result = await read.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(LibraryCodeReason.Cancelled, result.Reason);
        Assert.Equal(1, handler.Attempts);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Retry_wait_does_not_consume_the_next_attempt_timeout()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address, adjacent: true, documentCount: 1);
        var attempts = 0;
        server.RequestReceived = request => server.Replies[request.Path] = Interlocked.Increment(ref attempts) == 1
            ? new(503, RetryAfter: "60") : new(Bytes: library.Documents["/src/file0.cs"]);
        var clock = new SourceRetryClock(autoAdvance: false);
        using var sources = new LibrarySources(false, library.Cache, httpsOnly: false) { RequestTime = clock };
        var read = sources.GetAsync(library.Dll);
        Assert.Equal(TimeSpan.FromSeconds(30), await clock.NextDelayAsync());
        Assert.Equal(TimeSpan.FromSeconds(60), await clock.NextDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(LibraryCodeKind.OriginalSources, (await read.WaitAsync(TimeSpan.FromSeconds(30))).Kind);
        Assert.Equal(2, attempts);
    }

    private sealed class RetryHandler(string stage, int status, string channel, bool succeeds) : HttpMessageHandler
    {
        private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        internal int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/" + stage + "/", StringComparison.Ordinal)
                || stage == "document" && request.RequestUri.AbsolutePath.StartsWith("/sources/", StringComparison.Ordinal))
            {
                var attempt = ++Attempts % 4;
                if (!succeeds || attempt != 0)
                {
                    var current = attempt == 0 ? status == 429 ? 503 : 429 : status;
                    if (channel != "response")
                        throw new HttpRequestException(channel == "tunnel" ? HttpRequestError.ProxyTunnelError : HttpRequestError.ConnectionError,
                            "retryable failure", statusCode: (HttpStatusCode)current);
                    return new HttpResponseMessage((HttpStatusCode)current);
                }
            }
            else if (request.RequestUri.AbsolutePath.Contains(".pdb/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
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
}
