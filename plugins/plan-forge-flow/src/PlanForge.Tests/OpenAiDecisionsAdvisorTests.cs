using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlanForge.Advisors;
using PlanForge.Advisors.OpenAiDecisions;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// What OpenAI's Decisions API adds to the shared HTTP transport, over recorded HTTP exchanges: its address, bearer
/// credential, body with a cacheable shared part, measurement, parsing, cache usage, status meanings and rates. The
/// transport's timeout and cancellation are JevAdvisorTests'.
/// </summary>
public sealed class OpenAiDecisionsAdvisorTests
{
    /// <summary>A sentinel credential that must never appear in failures or bodies.</summary>
    private const string KEY = "secret-openai-key-never-in-diagnostics";

    /// <summary>The default requested model, deliberately different from the served model.</summary>
    private const string MODEL = "gpt-6-luna";

    /// <summary>A complete response in the shape the live API returned on 2026-11-01, with cache usage.</summary>
    private const string SUCCESS = """
        {"model":"gpt-6-luna-2026-10-15","answers":[{"type":"choice","name":"relevance","choice":"yes","probabilities":[{"value":"yes","probability":0.8},{"value":"no","probability":0.2}],"confidence":0.6}],"usage":{"input_tokens":3000,"output_tokens":2,"input_tokens_details":{"cached_tokens":2759,"cache_write_tokens":0}}}
        """;

    /// <summary>Only the fixed endpoint receives the bearer credential, and the body is exactly the decided wire shape.</summary>
    [Fact]
    public async Task Request_has_the_endpoint_bearer_header_and_exact_body()
    {
        var handler = new StubHandler();
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, handler);
        await advisor.AskAsync(new(new JsonObject { ["requirements"] = "Мир \"q\"" }, new JsonObject { ["item"] = "x" }, Questions()),
                               CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/decisions", sent.Url);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("Bearer " + KEY, sent.Authorization);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal("""{"model":"gpt-6-luna","input":[{"role":"user","content":[{"type":"input_text","text":"{\"requirements\":\"Мир \\\"q\\\"\"}","prompt_cache_breakpoint":{"mode":"explicit"}},{"type":"input_text","text":"{\"item\":\"x\"}"}]}],"questions":[{"type":"choice","name":"relevance","instructions":"Does it apply?","choices":[{"value":"yes","description":"It applies"},{"value":"no","description":"It does not"}]}],"prompt_cache_options":{"mode":"explicit"}}""",
                     sent.Body);
        Assert.DoesNotContain(KEY, sent.Body);
    }

    /// <summary>
    /// The shared part is the first text and the only one with a breakpoint, the request part follows without one, and each
    /// text is the compact JSON of its part, so only the shared part can be written to the cache.
    /// </summary>
    [Fact]
    public async Task The_shared_part_carries_the_only_cache_breakpoint()
    {
        var handler = new StubHandler();
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, handler);
        var shared = new JsonObject { ["requirements"] = new JsonArray("R1", "R2"), ["exclusions"] = new JsonArray(), ["changePoints"] = "c" };
        var item = new JsonObject { ["item"] = new JsonObject { ["text"] = "<T> & 世界" } };
        await advisor.AskAsync(new(shared, item, Questions()), CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var message = Assert.Single(body.RootElement.GetProperty("input").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var parts = message.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Equal("""{"requirements":["R1","R2"],"exclusions":[],"changePoints":"c"}""", parts[0].GetProperty("text").GetString());
        Assert.Equal("""{"item":{"text":"<T> & 世界"}}""", parts[1].GetProperty("text").GetString());
        Assert.Equal("explicit", parts[0].GetProperty("prompt_cache_breakpoint").GetProperty("mode").GetString());
        Assert.False(parts[1].TryGetProperty("prompt_cache_breakpoint", out _));
        Assert.Equal(1, Regex.Count(Assert.Single(handler.Requests).Body, "prompt_cache_breakpoint"));
        Assert.Equal("explicit", body.RootElement.GetProperty("prompt_cache_options").GetProperty("mode").GetString());
        Assert.False(body.RootElement.TryGetProperty("prompt_cache_key", out _));
    }

    /// <summary>The total is the sent body's UTF-8 bytes divided by three, rounded up, escaping and Cyrillic included.</summary>
    [Fact]
    public async Task Measure_matches_the_sent_body()
    {
        var handler = new StubHandler();
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, handler);
        var request = Request("Привет \"мир\" C:\\repo\n<T> 世界");
        var size = advisor.Measure(request);
        Assert.Empty(handler.Requests);
        await advisor.AskAsync(request, CancellationToken.None);
        Assert.Equal((Encoding.UTF8.GetByteCount(Assert.Single(handler.Requests).Body) + 2) / 3, size.Total);
    }

    /// <summary>The state-plus-question bound is the serialized <c>input</c> value plus the largest serialized question.</summary>
    [Fact]
    public async Task Measure_matches_input_plus_the_largest_serialized_question()
    {
        var request = new AdviceRequest(new JsonObject { ["requirements"] = "Shared state 世界\n" }, new JsonObject { ["item"] = "мир" },
        [
            new("short", "Short", [new("a", "A")]),
            new("largest", new string('長', 80), [new("yes", "A long description <T>\n"), new("no", "No")]),
            new("medium", "Medium sized question", [new("a", "One"), new("b", "Two")])
        ]);
        var handler = new StubHandler();
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, handler);
        var measure = advisor.Measure(request);
        await advisor.AskAsync(request, CancellationToken.None);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var inputBytes = Encoding.UTF8.GetByteCount(body.RootElement.GetProperty("input").GetRawText());
        var questions = body.RootElement.GetProperty("questions").EnumerateArray().ToArray();
        Assert.Equal(["short", "largest", "medium"], questions.Select(question => question.GetProperty("name").GetString()));
        var sizes = questions.Select(question => Encoding.UTF8.GetByteCount(question.GetRawText())).ToArray();
        Assert.True(sizes[1] > sizes[0] && sizes[1] > sizes[2]);
        Assert.Equal((inputBytes + sizes.Max() + 2) / 3, measure.StatePlusLargestQuestion);
    }

    /// <summary>The choice, its distribution keyed by value, confidence, served model and both cache counts are preserved.</summary>
    [Fact]
    public async Task A_200_maps_choices_confidence_model_and_cache_usage()
    {
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, Respond(200, SUCCESS.Replace("\"cache_write_tokens\":0",
            "\"cache_write_tokens\":41", StringComparison.Ordinal)));
        var result = await advisor.AskAsync(Request(), CancellationToken.None);
        var answer = Assert.IsType<Answered>(Assert.Single(result.Answers, pair => pair.Key == "relevance").Value);
        Assert.Equal("yes", answer.Choice);
        Assert.Equal(["yes", "no"], answer.Probabilities.Keys);
        Assert.Equal(0.8, answer.Probabilities["yes"]);
        Assert.Equal(0.2, answer.Probabilities["no"]);
        Assert.Equal(0.6, answer.Confidence);
        Assert.Equal("gpt-6-luna-2026-10-15", result.ServedModel);
        Assert.Equal(new AdviceUsage(3000, 2, 2759, 41), result.Usage);
    }

    /// <summary>A refusal answer is a refusal of the question it names, beside the other answers.</summary>
    [Fact]
    public async Task A_refusal_answer_is_refused()
    {
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, Respond(200, SUCCESS.Replace("\"answers\":[",
            "\"answers\":[{\"type\":\"refusal\",\"name\":\"other\"},", StringComparison.Ordinal)));
        var result = await advisor.AskAsync(Request(), CancellationToken.None);
        Assert.IsType<Refused>(result.Answers["other"]);
        Assert.IsType<Answered>(result.Answers["relevance"]);
    }

    /// <summary>Extra fields the probe saw, and others, are ignored; the cache counts are still read.</summary>
    [Fact]
    public async Task Unknown_fields_are_ignored()
    {
        const string body = """
            {"id":"dec_1","object":"decision","model":"gpt-6-luna","answers":[{"type":"choice","name":"relevance","choice":"no","probabilities":[{"value":"yes","probability":0.1,"extra":1},{"value":"no","probability":0.9}],"confidence":0.7,"rationale":null}],"usage":{"input_tokens":2800,"output_tokens":1,"total_tokens":2801,"input_tokens_details":{"cached_tokens":0,"cache_write_tokens":2759,"cache_write_tokens_details":{"ttl_30m":2759}},"output_tokens_details":{"reasoning_tokens":0}}}
            """;
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, Respond(200, body));
        var result = await advisor.AskAsync(Request(), CancellationToken.None);
        Assert.Equal("no", Assert.IsType<Answered>(result.Answers["relevance"]).Choice);
        Assert.Equal(new AdviceUsage(2800, 1, 0, 2759), result.Usage);
    }

    /// <summary>Usage the service does not report stays unknown, and so do cache counts it leaves out.</summary>
    [Fact]
    public async Task Missing_usage_is_null()
    {
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, Respond(200, SUCCESS[..SUCCESS.IndexOf(",\"usage\"", StringComparison.Ordinal)] + "}"));
        Assert.Null((await advisor.AskAsync(Request(), CancellationToken.None)).Usage);
        using var withoutDetails = new OpenAiDecisionsAdvisor(KEY, MODEL, Respond(200, SUCCESS.Replace(
            ",\"input_tokens_details\":{\"cached_tokens\":2759,\"cache_write_tokens\":0}", "", StringComparison.Ordinal)));
        Assert.Equal(new AdviceUsage(3000, 2, null, null), (await withoutDetails.AskAsync(Request(), CancellationToken.None)).Usage);
    }

    /// <summary>A 200 that is not JSON is an invalid response.</summary>
    [Fact]
    public Task A_200_that_is_not_json_is_invalid_response() => AssertFailure(200, AdvisorFailureKind.InvalidResponse, KEY);

    /// <summary>A 200 without a served model is an invalid response.</summary>
    [Fact]
    public Task A_missing_model_is_invalid_response() => AssertFailure(200, AdvisorFailureKind.InvalidResponse,
        SUCCESS.Replace("\"model\":\"gpt-6-luna-2026-10-15\",", "", StringComparison.Ordinal));

    /// <summary>A blank served model is an invalid response.</summary>
    [Fact]
    public Task A_blank_model_is_invalid_response() => AssertFailure(200, AdvisorFailureKind.InvalidResponse,
        SUCCESS.Replace("gpt-6-luna-2026-10-15", " ", StringComparison.Ordinal));

    /// <summary>Answers keyed by name, systemone's shape, or missing, are an invalid response.</summary>
    /// <param name="answers">The replacement for the answers member.</param>
    [Theory]
    [InlineData("\"answers\":{\"relevance\":{\"type\":\"choice\",\"choice\":\"yes\",\"probabilities\":[]}}")]
    [InlineData("\"answers\":null")]
    [InlineData("\"answers\":\"relevance\"")]
    public Task Answers_that_are_not_an_array_are_invalid_response(string answers) => AssertFailure(200,
        AdvisorFailureKind.InvalidResponse, "{\"model\":\"gpt-6-luna\"," + answers + "}");

    /// <summary>An answer type other than choice or refusal, such as a score, is an invalid response.</summary>
    /// <param name="type">The answer's type member.</param>
    [Theory]
    [InlineData("\"type\":\"score\"")]
    [InlineData("\"type\":\"Choice\"")]
    [InlineData("\"type\":null")]
    public Task An_answer_of_another_type_is_invalid_response(string type) => AssertFailure(200, AdvisorFailureKind.InvalidResponse,
        SUCCESS.Replace("\"type\":\"choice\"", type, StringComparison.Ordinal));

    /// <summary>An answer that names no question, a null answer included, is an invalid response.</summary>
    /// <param name="answers">The answers array.</param>
    [Theory]
    [InlineData("[{\"type\":\"refusal\"}]")]
    [InlineData("[{\"type\":\"refusal\",\"name\":null}]")]
    [InlineData("[null]")]
    public Task An_answer_without_a_name_is_invalid_response(string answers) => AssertFailure(200, AdvisorFailureKind.InvalidResponse,
        "{\"model\":\"gpt-6-luna\",\"answers\":" + answers + "}");

    /// <summary>Two answers naming one question, whatever their types, are an invalid response.</summary>
    [Fact]
    public Task Two_answers_with_one_name_are_invalid_response() => AssertFailure(200, AdvisorFailureKind.InvalidResponse,
        SUCCESS.Replace("\"answers\":[", "\"answers\":[{\"type\":\"refusal\",\"name\":\"relevance\"},", StringComparison.Ordinal));

    /// <summary>A choice that is a number, null or missing is an invalid response.</summary>
    /// <param name="choice">The replacement for the choice member.</param>
    [Theory]
    [InlineData("\"choice\":1,")]
    [InlineData("\"choice\":null,")]
    [InlineData("")]
    public Task A_choice_that_is_not_a_string_is_invalid_response(string choice) => AssertFailure(200,
        AdvisorFailureKind.InvalidResponse, SUCCESS.Replace("\"choice\":\"yes\",", choice, StringComparison.Ordinal));

    /// <summary>A choice answer without probabilities is an invalid response; a refusal needs none.</summary>
    [Fact]
    public Task Missing_probabilities_are_invalid_response() => AssertFailure(200, AdvisorFailureKind.InvalidResponse,
        SUCCESS.Replace("\"probabilities\":[{\"value\":\"yes\",\"probability\":0.8},{\"value\":\"no\",\"probability\":0.2}],", "",
                        StringComparison.Ordinal));

    /// <summary>A probability whose value is a number, null, missing, or the entry itself null, is an invalid response.</summary>
    /// <param name="entry">The replacement for the first probability entry.</param>
    [Theory]
    [InlineData("{\"value\":1,\"probability\":0.8}")]
    [InlineData("{\"value\":null,\"probability\":0.8}")]
    [InlineData("{\"probability\":0.8}")]
    [InlineData("null")]
    public Task A_probability_value_that_is_not_a_string_is_invalid_response(string entry) => AssertFailure(200,
        AdvisorFailureKind.InvalidResponse, SUCCESS.Replace("{\"value\":\"yes\",\"probability\":0.8}", entry, StringComparison.Ordinal));

    /// <summary>A distribution naming one value twice is an invalid response, rather than one probability silently winning.</summary>
    [Fact]
    public Task A_probability_value_named_twice_is_invalid_response() => AssertFailure(200, AdvisorFailureKind.InvalidResponse,
        SUCCESS.Replace("{\"value\":\"no\",\"probability\":0.2}", "{\"value\":\"yes\",\"probability\":0.2}", StringComparison.Ordinal));

    /// <summary>A negative cached or written count is an invalid response.</summary>
    /// <param name="details">The replacement input token details.</param>
    [Theory]
    [InlineData("{\"cached_tokens\":-1,\"cache_write_tokens\":0}")]
    [InlineData("{\"cached_tokens\":0,\"cache_write_tokens\":-1}")]
    [InlineData("{\"cached_tokens\":-1}")]
    public Task A_negative_cache_count_is_invalid_response(string details) => AssertFailure(200, AdvisorFailureKind.InvalidResponse,
        SUCCESS.Replace("{\"cached_tokens\":2759,\"cache_write_tokens\":0}", details, StringComparison.Ordinal));

    /// <summary>Cached plus written above the input count is an invalid response; exactly the input count is not.</summary>
    [Fact]
    public async Task Cache_counts_above_input_are_invalid_response()
    {
        await AssertFailure(200, AdvisorFailureKind.InvalidResponse,
            SUCCESS.Replace("{\"cached_tokens\":2759,\"cache_write_tokens\":0}", "{\"cached_tokens\":2759,\"cache_write_tokens\":242}",
                            StringComparison.Ordinal));
        await AssertFailure(200, AdvisorFailureKind.InvalidResponse,
            SUCCESS.Replace("{\"cached_tokens\":2759,\"cache_write_tokens\":0}", "{\"cache_write_tokens\":3001}", StringComparison.Ordinal));
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, Respond(200, SUCCESS.Replace(
            "{\"cached_tokens\":2759,\"cache_write_tokens\":0}", "{\"cached_tokens\":2759,\"cache_write_tokens\":241}", StringComparison.Ordinal)));
        Assert.Equal(new AdviceUsage(3000, 2, 2759, 241), (await advisor.AskAsync(Request(), CancellationToken.None)).Usage);
    }

    /// <summary>
    /// Cache counts whose mathematical sum passes <see cref="long.MaxValue"/> are still above the input count, whichever
    /// count is the large one and even at the largest input; the largest input fully cached is not.
    /// </summary>
    [Fact]
    public async Task Cache_counts_whose_sum_overflows_are_invalid_response()
    {
        const string DETAILS = "{\"cached_tokens\":2759,\"cache_write_tokens\":0}";
        await AssertFailure(200, AdvisorFailureKind.InvalidResponse,
            SUCCESS.Replace(DETAILS, "{\"cached_tokens\":9223372036854775807,\"cache_write_tokens\":1}", StringComparison.Ordinal));
        await AssertFailure(200, AdvisorFailureKind.InvalidResponse,
            SUCCESS.Replace(DETAILS, "{\"cached_tokens\":1,\"cache_write_tokens\":9223372036854775807}", StringComparison.Ordinal));
        var largest = SUCCESS.Replace("\"input_tokens\":3000", "\"input_tokens\":9223372036854775807", StringComparison.Ordinal);
        await AssertFailure(200, AdvisorFailureKind.InvalidResponse,
            largest.Replace(DETAILS, "{\"cached_tokens\":9223372036854775807,\"cache_write_tokens\":1}", StringComparison.Ordinal));
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, Respond(200, largest.Replace(
            DETAILS, "{\"cached_tokens\":9223372036854775807,\"cache_write_tokens\":0}", StringComparison.Ordinal)));
        Assert.Equal(new AdviceUsage(long.MaxValue, 2, long.MaxValue, 0),
                     (await advisor.AskAsync(Request(), CancellationToken.None)).Usage);
    }

    /// <summary>A credential that cannot be a header value fails as unauthorized before anything is sent.</summary>
    [Fact]
    public async Task A_key_that_cannot_be_a_header_is_unauthorized_and_sends_nothing()
    {
        var handler = new StubHandler();
        using var advisor = new OpenAiDecisionsAdvisor(KEY + "\nInjected: value", MODEL, handler, NoDelay);
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)),
            AdvisorFailureKind.Unauthorized);
        Assert.Empty(handler.Requests);
    }

    /// <summary>A 401 echoing a masked key fragment yields only the fixed unauthorized text, with no inner exception.</summary>
    [Fact]
    public async Task A_401_body_never_reaches_the_exception()
    {
        const string body = """
            {"error":{"message":"Incorrect API key provided: sk-proj-****lmno. You can find your API key at https://platform.openai.com/account/api-keys.","type":"invalid_request_error","param":null,"code":"invalid_api_key"}}
            """;
        var handler = Respond(401, body);
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, handler, NoDelay);
        var failure = await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None));
        AssertSanitized(failure, AdvisorFailureKind.Unauthorized);
        Assert.Equal("Advisor authentication failed.", failure.Message);
        Assert.DoesNotContain("sk-proj", failure.ToString());
        Assert.DoesNotContain("lmno", failure.ToString());
        Assert.Single(handler.Requests);
    }

    /// <summary>A 429 naming an exhausted quota by its type, or a spend or usage limit by its exact code, is forbidden and not retried.</summary>
    /// <param name="body">The 429 body.</param>
    [Theory]
    [InlineData("""{"error":{"type":"insufficient_quota","code":null,"message":"You exceeded your current quota."}}""")]
    [InlineData("""{"error":{"type":"requests","code":"insufficient_quota","message":"m"}}""")]
    [InlineData("""{"error":{"code":"credit_balance_exhausted"}}""")]
    [InlineData("""{"error":{"code":"organization_spend_limit_exceeded"}}""")]
    [InlineData("""{"error":{"code":"project_spend_limit_exceeded"}}""")]
    [InlineData("""{"error":{"code":"organization_usage_limit_exceeded"}}""")]
    public async Task Quota_429_is_forbidden_after_one_attempt(string body)
    {
        var waits = new List<TimeSpan>();
        var handler = Respond(429, body);
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, handler, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)),
            AdvisorFailureKind.Forbidden);
        Assert.Single(handler.Requests);
        Assert.Empty(waits);
    }

    /// <summary>
    /// Any other 429 is rate limiting, retried twice: an unrecognised body, a quota word only in the message, a code in
    /// another letter case, or an error that is not an object.
    /// </summary>
    /// <param name="body">The 429 body.</param>
    [Theory]
    [InlineData("""{"error":{"type":"requests","code":"rate_limit_exceeded","message":"Rate limit reached"}}""")]
    [InlineData("""{"error":{"code":"rate_limit_exceeded","message":"insufficient_quota"}}""")]
    [InlineData("""{"error":{"type":"Insufficient_Quota","code":"INSUFFICIENT_QUOTA"}}""")]
    [InlineData("""{"error":"insufficient_quota"}""")]
    [InlineData("""{"type":"insufficient_quota"}""")]
    [InlineData("insufficient_quota")]
    [InlineData("")]
    public async Task Ordinary_429_is_retried_twice(string body)
    {
        var waits = new List<TimeSpan>();
        var handler = Respond(429, body);
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, handler, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)),
            AdvisorFailureKind.RateLimited);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
    }

    /// <summary>A 503 is overload, retried twice; a 503 then a 200 succeeds.</summary>
    [Fact]
    public async Task A_503_is_retried_as_overloaded()
    {
        var handler = Respond(503, KEY);
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, handler, NoDelay);
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)),
            AdvisorFailureKind.Overloaded);
        Assert.Equal(3, handler.Requests.Count);
        var recovering = new StubHandler((attempt, _) => Task.FromResult(Response(attempt == 1 ? 503 : 200, attempt == 1 ? KEY : SUCCESS)));
        using var recovered = new OpenAiDecisionsAdvisor(KEY, MODEL, recovering, NoDelay);
        Assert.Equal("gpt-6-luna-2026-10-15", (await recovered.AskAsync(Request(), CancellationToken.None)).ServedModel);
        Assert.Equal(2, recovering.Requests.Count);
    }

    /// <summary>A 400 naming a context-length or token limit in its code or message is too large; any other 400 is rejected.</summary>
    /// <param name="body">The 400 body.</param>
    /// <param name="expected">The name of the expected failure kind.</param>
    [Theory]
    [InlineData("""{"error":{"code":"context_length_exceeded","message":"m"}}""", nameof(AdvisorFailureKind.TooLarge))]
    [InlineData("""{"error":{"code":"invalid_request","message":"Too many TOKENS in input"}}""", nameof(AdvisorFailureKind.TooLarge))]
    [InlineData("""{"error":{"code":"invalid_request","message":"Unknown parameter: questions[0].kind"}}""", nameof(AdvisorFailureKind.Rejected))]
    [InlineData("""{"error":{"type":"token"}}""", nameof(AdvisorFailureKind.Rejected))]
    [InlineData("token", nameof(AdvisorFailureKind.Rejected))]
    public Task A_400_is_too_large_only_when_it_names_a_token_limit(string body, string expected) =>
        AssertFailure(400, Enum.Parse<AdvisorFailureKind>(expected), body);

    /// <summary>The identity, rates and limits are reported without making a request.</summary>
    [Fact]
    public void Identity_rates_and_limits_need_no_request()
    {
        var handler = new StubHandler();
        using var advisor = new OpenAiDecisionsAdvisor(KEY, "gpt-6-luna-2026-10-15", handler);
        Assert.Equal("openai-decisions", advisor.Id);
        Assert.Equal("gpt-6-luna-2026-10-15", advisor.RequestedModel);
        Assert.Equal(0.10m, advisor.InputUsdPerMillion);
        Assert.Equal(0.01m, advisor.CacheReadUsdPerMillion);
        Assert.Equal(0.125m, advisor.CacheWriteUsdPerMillion);
        Assert.Equal(new AdvisorLimits("estimated_tokens", 64_000, 32_000, 64, 255, 8, TimeSpan.FromSeconds(30)), advisor.Limits);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Held as IAdvisor, the adapter's three rates reach the one pricing rule.</summary>
    [Fact]
    public void Rates_reach_pricing_through_IAdvisor()
    {
        using var adapter = new OpenAiDecisionsAdvisor(KEY, MODEL, new StubHandler());
        IAdvisor advisor = adapter;
        Assert.Equal(0.0895m, AdvisorPrice.Usd(advisor, 1_000_000, 200_000, 300_000));
        Assert.Equal(0.01m, advisor.CacheReadUsdPerMillion);
        Assert.Equal(0.125m, advisor.CacheWriteUsdPerMillion);
    }

    /// <summary>The ordered question every request asks.</summary>
    private static IReadOnlyList<ChoiceQuestion> Questions() =>
        [new("relevance", "Does it apply?", [new("yes", "It applies"), new("no", "It does not")])];

    /// <summary>Creates a simple ordered request with a shared part and an item.</summary>
    /// <param name="text">The shared state text.</param>
    private static AdviceRequest Request(string text = "context") =>
        new(new JsonObject { ["requirements"] = text }, new JsonObject { ["item"] = "one" }, Questions());

    /// <summary>Creates a handler that always returns the supplied response.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The body to return.</param>
    private static StubHandler Respond(int status, string body) => new((_, _) => Task.FromResult(Response(status, body)));

    /// <summary>Creates a response containing a body that must not leak through a failure.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The body to return.</param>
    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    /// <summary>Skips a retry wait while honoring cancellation.</summary>
    /// <param name="wait">The requested retry wait.</param>
    /// <param name="ct">The call's linked cancellation token.</param>
    private static Task NoDelay(TimeSpan wait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Checks one non-retryable failure after exactly one attempt, including diagnostic sanitization.</summary>
    /// <param name="status">The status returned by the stub.</param>
    /// <param name="kind">The expected service-independent failure.</param>
    /// <param name="body">The body to return.</param>
    private static async Task AssertFailure(int status, AdvisorFailureKind kind, string body)
    {
        var handler = Respond(status, body);
        using var advisor = new OpenAiDecisionsAdvisor(KEY, MODEL, handler, NoDelay);
        AssertSanitized(await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None)), kind);
        Assert.Single(handler.Requests);
    }

    /// <summary>Checks that every service failure is a fixed message with no retained sensitive cause.</summary>
    /// <param name="failure">The adapter's failure.</param>
    /// <param name="kind">The expected category.</param>
    private static void AssertSanitized(AdvisorException failure, AdvisorFailureKind kind)
    {
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(new AdvisorException(kind).Message, failure.Message);
        Assert.DoesNotContain(KEY, failure.ToString());
        Assert.Null(failure.InnerException);
    }

    /// <summary>A captured request, detached from the disposed HTTP objects.</summary>
    /// <param name="Url">The destination.</param>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Authorization">The request-specific authorization header.</param>
    /// <param name="ContentType">The request body's media type.</param>
    /// <param name="Body">The exact UTF-8 body decoded as text.</param>
    private sealed record SentRequest(string? Url, string Method, string? Authorization, string? ContentType, string Body);

    /// <summary>A deterministic HTTP transport that records every attempt before returning or failing.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        /// <summary>The response or failure for each one-based attempt.</summary>
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;

        /// <summary>Requests in send order.</summary>
        public List<SentRequest> Requests { get; } = [];

        /// <summary>Creates a recording transport with an optional response behavior.</summary>
        /// <param name="respond">The per-attempt response, defaulting to a successful Choice answer.</param>
        public StubHandler(Func<int, CancellationToken, Task<HttpResponseMessage>>? respond = null)
            => _respond = respond ?? ((_, _) => Task.FromResult(Response(200, SUCCESS)));

        /// <summary>Captures a request and invokes the next stub response.</summary>
        /// <param name="request">The outgoing request.</param>
        /// <param name="cancellationToken">The timeout and caller cancellation token.</param>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri?.AbsoluteUri, request.Method.Method, request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return await _respond(Requests.Count, cancellationToken);
        }
    }
}
