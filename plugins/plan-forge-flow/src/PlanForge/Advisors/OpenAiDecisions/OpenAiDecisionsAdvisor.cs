using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PlanForge.Advisors.OpenAiDecisions;

/// <summary>
/// OpenAI's Decisions API on the shared HTTP transport: the shared state and the request's state as two <c>input_text</c>
/// parts of one user message, the first an explicit prompt-cache prefix, with a bearer credential and no pacing.
/// </summary>
internal sealed class OpenAiDecisionsAdvisor : HttpAdvisor
{
    /// <summary>The only endpoint to which this adapter sends content or authentication.</summary>
    private const string ENDPOINT = "https://api.openai.com/v1/decisions";

    /// <summary>The 429 error codes that name an exhausted quota or a spend limit rather than rate limiting.</summary>
    private static readonly HashSet<string> EXHAUSTED_CODES = new(StringComparer.Ordinal)
    {
        "insufficient_quota", "credit_balance_exhausted", "organization_spend_limit_exceeded",
        "project_spend_limit_exceeded", "organization_usage_limit_exceeded"
    };

    /// <summary>The cache mode on the shared part's breakpoint and on the request's options.</summary>
    private static readonly OpenAiDecisionsCacheMode EXPLICIT = new("explicit");

    /// <summary>The credential, used only on an outgoing request's header.</summary>
    private readonly string _key;

    /// <summary>Creates the adapter without probing or sending any request.</summary>
    /// <param name="key">The credential read by the server from its environment.</param>
    /// <param name="model">The model requested by the server.</param>
    /// <param name="handler">An optional owned transport for deterministic tests.</param>
    /// <param name="delay">An optional cancellable retry wait for deterministic tests.</param>
    /// <param name="clock">The timeout clock, defaulting to the system clock.</param>
    public OpenAiDecisionsAdvisor(string key, string model, HttpMessageHandler? handler = null,
                                  Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? clock = null)
        : base(new Uri(ENDPOINT), model, handler, delay, clock) => _key = key;

    /// <summary>The stable Advisor identifier.</summary>
    public override string Id => "openai-decisions";

    /// <summary>The uncached input price OpenAI published for gpt-6-luna up to 272K input tokens, checked 2026-10-31; output is free.</summary>
    public override decimal InputUsdPerMillion => 0.10m;

    /// <summary>The cache-read input price OpenAI published for gpt-6-luna, checked 2026-10-31.</summary>
    public override decimal CacheReadUsdPerMillion => 0.01m;

    /// <summary>The cache-write input price OpenAI published for gpt-6-luna with the 30-minute lifetime, checked 2026-10-31.</summary>
    public override decimal CacheWriteUsdPerMillion => 0.125m;

    /// <summary>The systemone bounds, in estimated tokens: UTF-8 bytes divided by three and rounded up.</summary>
    public override AdvisorLimits Limits { get; } = new("estimated_tokens", 64_000, 32_000, 64, 255, 8, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Measures the same serialized body sent by AskAsync, without contacting the service: the whole body, and the serialized
    /// <c>input</c> value plus the largest serialized question.
    /// </summary>
    /// <param name="request">The state and ordered questions to measure.</param>
    public override AdviceSize Measure(AdviceRequest request)
    {
        var body = Body(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, OpenAiDecisionsJson.Wire.OpenAiDecisionsRequest);
        var input = JsonSerializer.SerializeToUtf8Bytes(body.Input, OpenAiDecisionsJson.Wire.OpenAiDecisionsMessageArray);
        var largest = body.Questions.Select(question =>
            JsonSerializer.SerializeToUtf8Bytes(question, OpenAiDecisionsJson.Wire.OpenAiDecisionsQuestion).Length).DefaultIfEmpty(0).Max();
        return new(EstimatedTokens(bytes.Length), EstimatedTokens(checked(input.Length + largest)));
    }

    /// <summary>Serializes the Decisions body, its non-ASCII text as written.</summary>
    /// <param name="request">The service-independent request.</param>
    protected override byte[] Serialize(AdviceRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(Body(request), OpenAiDecisionsJson.Wire.OpenAiDecisionsRequest);

    /// <summary>Reads the content of a 200, and of a 400 or 429 so that their error can be classified.</summary>
    /// <param name="status">The HTTP status code.</param>
    protected override bool ReadsBody(int status) => status is 200 or 400 or 429;

    /// <summary>Sends the credential as a bearer token; a value that cannot be a header throws FormatException.</summary>
    /// <param name="message">The attempt's request.</param>
    protected override void Authenticate(HttpRequestMessage message) => message.Headers.Add("Authorization", "Bearer " + _key);

    /// <summary>
    /// Classifies every HTTP status without exposing the response's content. Only a 400's size error and a 429's exhausted
    /// quota or spend limit are read from the body; a 429 that names neither is rate limiting.
    /// </summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The content of a 400 or 429, used only to read its error's type and code.</param>
    protected override AdvisorFailureKind Failure(int status, string body) => status switch
    {
        400 => Error(body) is var error && (Names(error.Code) || Names(error.Message))
            ? AdvisorFailureKind.TooLarge : AdvisorFailureKind.Rejected,
        401 => AdvisorFailureKind.Unauthorized,
        403 or 404 => AdvisorFailureKind.Forbidden,
        408 => AdvisorFailureKind.TimedOut,
        429 => Error(body) is var error && (error.Type == "insufficient_quota" || error.Code is { } code && EXHAUSTED_CODES.Contains(code))
            ? AdvisorFailureKind.Forbidden : AdvisorFailureKind.RateLimited,
        503 => AdvisorFailureKind.Overloaded,
        >= 500 and < 600 => AdvisorFailureKind.Unavailable,
        _ => AdvisorFailureKind.InvalidResponse
    };

    /// <summary>Maps the wire response, leaving answer matching, probability sums and confidence bounds to the core.</summary>
    /// <param name="body">The response JSON, never retained by an exception.</param>
    protected override AdviceResult Parse(string body)
    {
        var response = JsonSerializer.Deserialize(body, OpenAiDecisionsJson.Default.OpenAiDecisionsResponse);
        if (response is null || string.IsNullOrWhiteSpace(response.Model) || response.Answers is null)
            throw new AdvisorException(AdvisorFailureKind.InvalidResponse);
        var answers = new Dictionary<string, AdviceOutcome>(StringComparer.Ordinal);
        foreach (var answer in response.Answers)
        {
            if (answer?.Name is not { } name || !answers.TryAdd(name, Outcome(answer)))
                throw new AdvisorException(AdvisorFailureKind.InvalidResponse);
        }
        return new(answers, response.Model, Usage(response.Usage));
    }

    /// <summary>Builds the ordered wire body, the two state parts as compact JSON texts.</summary>
    /// <param name="request">The service-independent request.</param>
    private OpenAiDecisionsRequest Body(AdviceRequest request) => new(RequestedModel,
        [new("user", [new("input_text", Compact(request.SharedState), EXPLICIT), new("input_text", Compact(request.RequestState))])],
        [.. request.Questions.Select(question => new OpenAiDecisionsQuestion("choice", question.Id, question.Instructions,
            [.. question.Options.Select(option => new OpenAiDecisionsChoice(option.Value, option.Description))]))],
        EXPLICIT);

    /// <summary>Writes one state part as compact JSON, its non-ASCII text as written.</summary>
    /// <param name="state">The JSON-object state part.</param>
    private static string Compact(JsonObject state) => state.ToJsonString(OpenAiDecisionsJson.Wire.Options);

    /// <summary>Rounds a UTF-8 byte count up to estimated tokens without integer overflow.</summary>
    /// <param name="bytes">The serialized UTF-8 byte count.</param>
    private static int EstimatedTokens(int bytes) => bytes / 3 + (bytes % 3 == 0 ? 0 : 1);

    /// <summary>Maps one named answer: a choice with its distribution, or a refusal; anything else is an invalid response.</summary>
    /// <param name="answer">The wire answer, already known to have a name.</param>
    private static AdviceOutcome Outcome(OpenAiDecisionsAnswer answer)
    {
        if (answer.Type == "refusal") return new Refused();
        if (answer.Type != "choice" || answer.Choice is null || answer.Probabilities is null)
            throw new AdvisorException(AdvisorFailureKind.InvalidResponse);
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var probability in answer.Probabilities)
        {
            if (probability?.Value is not { } value || !probabilities.TryAdd(value, probability.Probability))
                throw new AdvisorException(AdvisorFailureKind.InvalidResponse);
        }
        return new Answered(answer.Choice, probabilities, answer.Confidence);
    }

    /// <summary>
    /// Maps reported usage, refusing negative cache counts and cache counts above the input count. The comparison
    /// subtracts instead of adding, so that two counts whose sum passes <see cref="long.MaxValue"/> are still refused.
    /// </summary>
    /// <param name="usage">The wire usage, or null when the response reported none.</param>
    private static AdviceUsage? Usage(OpenAiDecisionsUsage? usage)
    {
        if (usage is null) return null;
        var cached = usage.InputTokensDetails?.CachedTokens;
        var written = usage.InputTokensDetails?.CacheWriteTokens;
        if (cached < 0 || written < 0
            || usage.InputTokens is { } input && ((cached ?? 0) > input || (written ?? 0) > input - (cached ?? 0)))
            throw new AdvisorException(AdvisorFailureKind.InvalidResponse);
        return new(usage.InputTokens, usage.OutputTokens, cached, written);
    }

    /// <summary>Whether an error field names a context-length or token limit, ignoring case.</summary>
    /// <param name="text">The error's code or message, or null when absent.</param>
    private static bool Names(string? text) => text is not null
        && (text.Contains("context_length", StringComparison.OrdinalIgnoreCase) || text.Contains("token", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the string fields of <c>{"error":{…}}</c>; a body that is not JSON, has no <c>error</c> member, or whose
    /// <c>error</c> is not an object is unrecognised and yields no fields.
    /// </summary>
    /// <param name="body">The response content, never retained.</param>
    private static OpenAiDecisionsError Error(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                ? new(Text(error, "type"), Text(error, "code"), Text(error, "message")) : OpenAiDecisionsError.None;
        }
        catch (JsonException)
        {
            return OpenAiDecisionsError.None;
        }
    }

    /// <summary>Reads one string property, or null when it is absent or not a string.</summary>
    /// <param name="error">The error object.</param>
    /// <param name="name">The property name.</param>
    private static string? Text(JsonElement error, string name) =>
        error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>The fields of an OpenAI error body that classify a failure; never retained beyond classification.</summary>
/// <param name="Type">The error's type, or null when absent.</param>
/// <param name="Code">The error's code, or null when absent.</param>
/// <param name="Message">The error's message, or null when absent.</param>
internal sealed record OpenAiDecisionsError(string? Type, string? Code, string? Message)
{
    /// <summary>An unrecognised body: no fields.</summary>
    public static OpenAiDecisionsError None { get; } = new(null, null, null);
}

/// <summary>The Decisions request body, in its exact wire property order.</summary>
/// <param name="Model">The requested model.</param>
/// <param name="Input">The one user message carrying the two state parts.</param>
/// <param name="Questions">Questions in request order.</param>
/// <param name="PromptCacheOptions">The explicit cache mode, so that only the marked prefix is written.</param>
internal sealed record OpenAiDecisionsRequest(string Model, OpenAiDecisionsMessage[] Input, OpenAiDecisionsQuestion[] Questions,
                                              OpenAiDecisionsCacheMode PromptCacheOptions);

/// <summary>One input message.</summary>
/// <param name="Role">The literal user role.</param>
/// <param name="Content">The message's text parts, in order.</param>
internal sealed record OpenAiDecisionsMessage(string Role, OpenAiDecisionsPart[] Content);

/// <summary>One <c>input_text</c> part, the shared one ending in an explicit cache breakpoint.</summary>
/// <param name="Type">The literal part type.</param>
/// <param name="Text">The part's compact JSON text.</param>
/// <param name="PromptCacheBreakpoint">The breakpoint that ends the cacheable prefix, or null and omitted for any other part.</param>
internal sealed record OpenAiDecisionsPart(string Type, string Text,
                                           [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                           OpenAiDecisionsCacheMode? PromptCacheBreakpoint = null);

/// <summary>A prompt-cache mode.</summary>
/// <param name="Mode">The literal mode.</param>
internal sealed record OpenAiDecisionsCacheMode(string Mode);

/// <summary>The wire form of one Choice question.</summary>
/// <param name="Type">The literal choice discriminator.</param>
/// <param name="Name">The identifier its answer names.</param>
/// <param name="Instructions">The question's exact instructions.</param>
/// <param name="Choices">The options in their original order.</param>
internal sealed record OpenAiDecisionsQuestion(string Type, string Name, string Instructions, OpenAiDecisionsChoice[] Choices);

/// <summary>The wire form of one option.</summary>
/// <param name="Value">The value the service may choose.</param>
/// <param name="Description">The meaning of this choice.</param>
internal sealed record OpenAiDecisionsChoice(string Value, string Description);

/// <summary>The response envelope with optional reported usage; unknown fields are ignored.</summary>
/// <param name="Model">The model that served the request.</param>
/// <param name="Answers">Answers, each naming its question.</param>
/// <param name="Usage">Reported token counts, when present.</param>
internal sealed record OpenAiDecisionsResponse(string? Model, List<OpenAiDecisionsAnswer?>? Answers, OpenAiDecisionsUsage? Usage);

/// <summary>A wire answer before the core validates it against its question.</summary>
/// <param name="Type">The answer discriminator: <c>choice</c> or <c>refusal</c>.</param>
/// <param name="Name">The question this answer names.</param>
/// <param name="Choice">The reported option value.</param>
/// <param name="Probabilities">The reported distribution, one entry per option.</param>
/// <param name="Confidence">Reported confidence, when present.</param>
internal sealed record OpenAiDecisionsAnswer(string? Type, string? Name, string? Choice,
                                             List<OpenAiDecisionsProbability?>? Probabilities, double? Confidence);

/// <summary>One option's reported probability.</summary>
/// <param name="Value">The option value.</param>
/// <param name="Probability">The reported probability.</param>
internal sealed record OpenAiDecisionsProbability(string? Value, double Probability);

/// <summary>Token counts using the Decisions wire names.</summary>
/// <param name="InputTokens">Reported input tokens.</param>
/// <param name="OutputTokens">Reported output tokens.</param>
/// <param name="InputTokensDetails">The cached and cache-written parts of the input, when present.</param>
internal sealed record OpenAiDecisionsUsage(long? InputTokens, long? OutputTokens, OpenAiDecisionsInputDetails? InputTokensDetails);

/// <summary>The parts of the input count the prompt cache served or stored.</summary>
/// <param name="CachedTokens">Input tokens read from the cache.</param>
/// <param name="CacheWriteTokens">Input tokens written to the cache.</param>
internal sealed record OpenAiDecisionsInputDetails(long? CachedTokens, long? CacheWriteTokens);

/// <summary>The complete reflection-free Decisions wire contract.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OpenAiDecisionsRequest))]
[JsonSerializable(typeof(OpenAiDecisionsMessage[]))]
[JsonSerializable(typeof(OpenAiDecisionsQuestion))]
[JsonSerializable(typeof(OpenAiDecisionsResponse))]
internal sealed partial class OpenAiDecisionsJson : JsonSerializerContext
{
    /// <summary>
    /// For the request body, its two state texts and its measurement: non-ASCII and HTML-sensitive text as written, not
    /// <c>\uXXXX</c>, as <c>SystemOneJson.Wire</c> does.
    /// </summary>
    internal static OpenAiDecisionsJson Wire =>
        field ??= new(new JsonSerializerOptions(Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
