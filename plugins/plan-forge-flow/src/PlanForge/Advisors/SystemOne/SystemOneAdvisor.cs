using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PlanForge.Advisors.SystemOne;

/// <summary>
/// The systemone wire protocol on the shared HTTP transport, shared below IAdvisor by the services that speak it. A
/// subclass owns its endpoint, credential, status meanings and any pacing; this class owns the body, its measurement and
/// limits, and parsing, while <see cref="HttpAdvisor"/> owns the client, the deadline and retries. It is a helper, never a
/// contract: the core sees only IAdvisor.
/// </summary>
internal abstract class SystemOneAdvisor : HttpAdvisor
{
    /// <summary>Creates the Advisor without probing or sending any request.</summary>
    /// <param name="endpoint">The only address that receives content or authentication.</param>
    /// <param name="model">The model requested on every attempt.</param>
    /// <param name="handler">An optional owned transport for deterministic tests.</param>
    /// <param name="delay">An optional cancellable retry wait for deterministic tests.</param>
    /// <param name="clock">The timeout clock, defaulting to the system clock.</param>
    protected SystemOneAdvisor(Uri endpoint, string model, HttpMessageHandler? handler,
                               Func<TimeSpan, CancellationToken, Task>? delay, TimeProvider? clock)
        : base(endpoint, model, handler, delay, clock)
    {
    }

    /// <summary>
    /// The protocol's bounds, the same at every service that speaks it, in estimated tokens: UTF-8 bytes divided by three
    /// and rounded up.
    /// </summary>
    public override AdvisorLimits Limits { get; } = new("estimated_tokens", 64_000, 32_000, 64, 255, 8, TimeSpan.FromSeconds(30));

    /// <summary>Measures the same serialized body sent by AskAsync, without contacting the service.</summary>
    /// <param name="request">The state and ordered questions to measure.</param>
    public override AdviceSize Measure(AdviceRequest request)
    {
        var body = Body(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, SystemOneJson.Wire.SystemOneRequest);
        using var state = new MemoryStream();
        using (var writer = new Utf8JsonWriter(state, new JsonWriterOptions { Encoder = SystemOneJson.Wire.Options.Encoder }))
            State(request).WriteTo(writer, SystemOneJson.Wire.Options);
        var largest = body.Questions.Values.Select(question =>
            JsonSerializer.SerializeToUtf8Bytes(question, SystemOneJson.Wire.SystemOneQuestion).Length).DefaultIfEmpty(0).Max();
        return new(EstimatedTokens(bytes.Length), EstimatedTokens(checked((int)state.Length + largest)));
    }

    /// <summary>Serializes the systemone body, its non-ASCII text as written.</summary>
    /// <param name="request">The service-independent request.</param>
    protected override byte[] Serialize(AdviceRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(Body(request), SystemOneJson.Wire.SystemOneRequest);

    /// <summary>Reads the content of a 200, and of a 400 or 422 so that a subclass can recognize a refusal.</summary>
    /// <param name="status">The HTTP status code.</param>
    protected override bool ReadsBody(int status) => status is 200 or 400 or 422;

    /// <summary>Builds ordered wire maps, leaving object-state serialization to the generated contract.</summary>
    /// <param name="request">The service-independent request.</param>
    private SystemOneRequest Body(AdviceRequest request)
    {
        var questions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal);
        foreach (var question in request.Questions)
            questions.Add(question.Id, new("choice", question.Instructions,
                question.Options.ToDictionary(option => option.Value, option => option.Description, StringComparer.Ordinal)));
        return new(State(request), RequestedModel, questions);
    }

    /// <summary>
    /// The one systemone <c>state</c>: the shared part's properties in order, then the request part's, each value
    /// deep-cloned so that neither part is reparented. A name in both parts throws, as
    /// <see cref="JsonObject.Add(string, JsonNode?)"/> does.
    /// </summary>
    /// <param name="request">The service-independent request whose two state parts are merged.</param>
    private static JsonObject State(AdviceRequest request)
    {
        var state = new JsonObject();
        foreach (var (name, value) in request.SharedState.Concat(request.RequestState))
            state.Add(name, value?.DeepClone());
        return state;
    }

    /// <summary>Rounds a UTF-8 byte count up to estimated tokens without integer overflow.</summary>
    /// <param name="bytes">The serialized UTF-8 byte count.</param>
    private static int EstimatedTokens(int bytes) => bytes / 3 + (bytes % 3 == 0 ? 0 : 1);

    /// <summary>Maps the wire response, leaving per-question validity to the core.</summary>
    /// <param name="body">The response JSON, never retained by an exception.</param>
    protected override AdviceResult Parse(string body)
    {
        var response = JsonSerializer.Deserialize(body, SystemOneJson.Default.SystemOneResponse);
        if (response is null || string.IsNullOrWhiteSpace(response.Model) || response.Answers is null)
            throw new AdvisorException(AdvisorFailureKind.InvalidResponse);
        var answers = new Dictionary<string, AdviceOutcome>(StringComparer.Ordinal);
        foreach (var (id, answer) in response.Answers)
        {
            if (answer is null || answer.Type != "choice" || answer.Choice is null || answer.Probabilities is null)
                throw new AdvisorException(AdvisorFailureKind.InvalidResponse);
            answers.Add(id, new Answered(answer.Choice, answer.Probabilities, answer.Confidence));
        }
        return new(answers, response.Model,
            response.Usage is { } usage ? new(usage.InputTokens, usage.OutputTokens) : null);
    }
}

/// <summary>The systemone request body, in its exact wire property order.</summary>
/// <param name="State">The supplied JSON object.</param>
/// <param name="Model">The requested model.</param>
/// <param name="Questions">Ordered questions keyed by their identifiers.</param>
internal sealed record SystemOneRequest(JsonObject State, string Model, Dictionary<string, SystemOneQuestion> Questions);

/// <summary>The wire form of one Choice question.</summary>
/// <param name="Type">The literal choice discriminator.</param>
/// <param name="Instructions">The question's exact instructions.</param>
/// <param name="Criteria">Option values and descriptions in their original order.</param>
internal sealed record SystemOneQuestion(string Type, string Instructions, Dictionary<string, string> Criteria);

/// <summary>The response envelope with optional reported usage.</summary>
/// <param name="Model">The model that served the request.</param>
/// <param name="Answers">Answers keyed by the question identifiers.</param>
/// <param name="Usage">Reported token counts, when present.</param>
internal sealed record SystemOneResponse(string? Model, Dictionary<string, SystemOneAnswer?>? Answers, SystemOneUsage? Usage);

/// <summary>A wire Choice answer before the core validates it against its question.</summary>
/// <param name="Type">The answer discriminator.</param>
/// <param name="Choice">The reported option value.</param>
/// <param name="Probabilities">The reported distribution.</param>
/// <param name="Confidence">Reported confidence, when present.</param>
internal sealed record SystemOneAnswer(string? Type, string? Choice, Dictionary<string, double>? Probabilities, double? Confidence);

/// <summary>Token counts using the systemone wire names.</summary>
/// <param name="InputTokens">Reported input tokens.</param>
/// <param name="OutputTokens">Reported output tokens.</param>
internal sealed record SystemOneUsage([property: JsonPropertyName("input_tokens")] long? InputTokens,
                                      [property: JsonPropertyName("output_tokens")] long? OutputTokens);

/// <summary>The complete reflection-free systemone wire contract.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SystemOneRequest))]
[JsonSerializable(typeof(SystemOneQuestion))]
[JsonSerializable(typeof(SystemOneResponse))]
internal sealed partial class SystemOneJson : JsonSerializerContext
{
    /// <summary>
    /// For the request body and its measurement: non-ASCII and HTML-sensitive text as written, not <c>\uXXXX</c>. The
    /// service reads the decoded text, and an escape would make the size bounds see a Cyrillic letter as six bytes.
    /// </summary>
    internal static SystemOneJson Wire =>
        field ??= new(new JsonSerializerOptions(Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
