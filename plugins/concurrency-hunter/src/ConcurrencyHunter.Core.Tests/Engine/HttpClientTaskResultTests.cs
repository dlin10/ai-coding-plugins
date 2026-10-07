using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What HttpClient's built-in models give the run through their tasks (R5): a new response or string, a response keeping the
/// content or the request it was sent with, the same through <c>.Result</c>.</summary>
public sealed class HttpClientTaskResultTests
{
    private const string PREFIX = "body:Fixture:M:Facts.";

    private static readonly Lazy<HeapRun> Run = new(Analyze, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Awaited_get_gives_one_new_response()
    {
        var response = Created(Single(Roles("Get", "response")));
        Assert.Contains("HttpResponseMessage", response.TypeKey, StringComparison.Ordinal);
        Assert.Equal(HeapRegionKind.Task, Heap.Regions[Single(Roles("Get", "t"))].Kind);
        Assert.NotEqual(response.Identity, Single(Roles("GetAgain", "response")));
    }

    [Theory]
    [InlineData("GetString")]
    [InlineData("ReadAsString")]
    public void Awaited_string_read_gives_a_new_string(string method)
    {
        var text = Created(Single(Roles(method, "text")));
        Assert.Equal("System.Private.CoreLib:string", text.TypeKey);
    }

    [Theory]
    [InlineData("Post")]
    [InlineData("Put")]
    public void Awaited_post_or_put_gives_a_new_response_keeping_the_content(string method)
    {
        var response = Created(Single(Roles(method, "response")));
        Assert.Equal(Single(Roles(method, "content")), Assert.Single(Heap.PointsTo(response.Identity, PathValue.KEPT)));
    }

    [Fact]
    public void Awaited_send_gives_a_new_response_keeping_the_request()
    {
        var response = Created(Single(Roles("Send", "response")));
        Assert.Equal(Single(Roles("Send", "request")), Assert.Single(Heap.PointsTo(response.Identity, PathValue.KEPT)));
    }

    [Theory]
    [InlineData("GetResult")]
    [InlineData("GetAwaiterResult")]
    public void Result_of_a_get_gives_the_same_new_response(string method)
    {
        var response = Created(Single(Roles(method, "response")));
        Assert.Contains("HttpResponseMessage", response.TypeKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_of_a_send_keeps_the_request()
    {
        var response = Created(Single(Roles("SendResult", "response")));
        Assert.Equal(Single(Roles("SendResult", "request")), Assert.Single(Heap.PointsTo(response.Identity, PathValue.KEPT)));
    }

    // ---- observing ----

    private static HeapSolution Heap => Run.Value.Heap;

    private static string Single(IReadOnlySet<string> regions) => Assert.Single(regions);

    /// <summary>The region, which a model made: neither a task nor an object of the source.</summary>
    /// <param name="identity">The region's identity.</param>
    private static HeapRegion Created(string identity)
    {
        var region = Heap.Regions[identity];
        Assert.NotEqual(HeapRegionKind.Task, region.Kind);
        Assert.NotNull(region.ModelCreationKey);
        return region;
    }

    /// <summary>The regions a local of a method of <c>Facts</c> holds.</summary>
    /// <param name="method">The method's name.</param>
    /// <param name="variable">The local's name.</param>
    private static IReadOnlySet<string> Roles(string method, string variable) =>
        Heap.Instances.Values.Where(instance => instance.BodyId == PREFIX + method)
            .SelectMany(instance => instance.Summary.Variables.Where(value => value.SymbolKey.Contains($"|{variable}|", StringComparison.Ordinal))
                                            .SelectMany(value => Heap.Resolve(instance.Id, value.Values)))
            .ToHashSet(StringComparer.Ordinal);

    // ---- the fixture ----

    private const string SOURCE = """
        public sealed class Facts
        {
            private readonly HttpClient _client = new HttpClient();

            public async Task Get()
            {
                var t = _client.GetAsync("https://example.org/");
                var response = await t;
                GC.KeepAlive(response);
            }

            public async Task GetAgain()
            {
                var response = await _client.GetAsync(new Uri("https://example.org/"), HttpCompletionOption.ResponseHeadersRead);
                GC.KeepAlive(response);
            }

            public async Task GetString()
            {
                var text = await _client.GetStringAsync("https://example.org/");
                GC.KeepAlive(text);
            }

            public async Task ReadAsString()
            {
                var text = await new StringContent("body").ReadAsStringAsync();
                GC.KeepAlive(text);
            }

            public async Task Post()
            {
                var content = new StringContent("body");
                var response = await _client.PostAsync("https://example.org/", content);
                GC.KeepAlive(response);
            }

            public async Task Put()
            {
                var content = new StringContent("body");
                var response = await _client.PutAsync(new Uri("https://example.org/"), content, CancellationToken.None);
                GC.KeepAlive(response);
            }

            public async Task Send()
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://example.org/");
                var response = await _client.SendAsync(request);
                GC.KeepAlive(response);
            }

            public void GetResult()
            {
                var response = _client.GetAsync("https://example.org/").Result;
                GC.KeepAlive(response);
            }

            public void GetAwaiterResult()
            {
                var response = _client.GetAsync("https://example.org/").GetAwaiter().GetResult();
                GC.KeepAlive(response);
            }

            public void SendResult()
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://example.org/");
                var response = _client.SendAsync(request).Result;
                GC.KeepAlive(response);
            }
        }

        public sealed class FactsController : ControllerBase
        {
            public async Task Get() => await new Facts().Get();
            public async Task GetAgain() => await new Facts().GetAgain();
            public async Task GetString() => await new Facts().GetString();
            public async Task ReadAsString() => await new Facts().ReadAsString();
            public async Task Post() => await new Facts().Post();
            public async Task Put() => await new Facts().Put();
            public async Task Send() => await new Facts().Send();
            public void GetResult() => new Facts().GetResult();
            public void GetAwaiterResult() => new Facts().GetAwaiterResult();
            public void SendResult() => new Facts().SendResult();
        }
        """;

    private static HeapRun Analyze() =>
        Solve(ReachScope(FixtureSolution.Create(("Case.cs", Usings + "using System.Net.Http;\n" + SOURCE + Startup())), "scope:Fixture"));
}
