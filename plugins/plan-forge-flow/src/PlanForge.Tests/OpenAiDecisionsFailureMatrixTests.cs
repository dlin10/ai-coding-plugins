using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PlanForge.Advisors;
using PlanForge.Advisors.OpenAiDecisions;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// OpenAI's failure classification (R5) walked as a matrix of status and error body, through the real
/// <see cref="OpenAiDecisionsAdvisor"/> over a stub handler that answers every attempt alike and a retry wait that returns at
/// once. Every expectation is written by hand from R5, never from the code under test.
/// </summary>
public sealed class OpenAiDecisionsFailureMatrixTests
{
    /// <summary>The cells known to be wider than their expectation: none, since every kind and attempt count must be exact.</summary>
    private static readonly string[] WIDER = [];

    /// <summary>Status axis: every status class, each status R5 names, and neighbours it does not.</summary>
    private static readonly int[] STATUSES = [101, 204, 302, 400, 401, 403, 404, 405, 408, 409, 413, 415, 422, 429, 500, 502, 503, 504, 529];

    /// <summary>Body axis, numbered from 1 as in the task: envelope shapes, size markers, quota markers and rate limits.</summary>
    private static readonly string[] BODIES =
    [
        // (1)–(8) Envelope shapes: empty, not JSON, no error, error not an object, other roots, markers only at the top level.
        "",
        "Service unavailable",
        """{"message":"x"}""",
        """{"error":"insufficient_quota"}""",
        """{"error":"too many tokens"}""",
        "\"too many tokens\"",
        """[{"error":{"code":"context_length_exceeded"}}]""",
        """{"code":"insufficient_quota","detail":"too many tokens"}""",
        // (9)–(17) Size markers: none, in code or message in either case, only in type, only in another property.
        """{"error":{"type":"invalid_request_error","code":null,"message":"Bad request."}}""",
        """{"error":{"type":"invalid_request_error","code":"context_length_exceeded","message":"Bad request."}}""",
        """{"error":{"type":"invalid_request_error","code":null,"message":"This request has too many tokens."}}""",
        """{"error":{"type":"invalid_request_error","code":"max_tokens_exceeded","message":"Bad request."}}""",
        """{"error":{"type":"invalid_request_error","code":null,"message":"context_length is 100"}}""",
        """{"error":{"type":"invalid_request_error","code":"Context_Length_Exceeded","message":"Bad request."}}""",
        """{"error":{"type":"invalid_request_error","code":null,"message":"TOO MANY TOKENS"}}""",
        """{"error":{"type":"tokens_exceeded","code":"invalid_value","message":"Bad request."}}""",
        """{"error":{"type":"invalid_request_error","code":"invalid_value","message":"Bad request.","param":"context_length"}}""",
        // (18)–(27) Quota markers: the exact type, each exact code, then the message, another type, another case, another property.
        """{"error":{"type":"insufficient_quota","code":null,"message":"Request refused."}}""",
        """{"error":{"type":"invalid_request_error","code":"insufficient_quota","message":"Request refused."}}""",
        """{"error":{"type":"requests","code":"credit_balance_exhausted","message":"Request refused."}}""",
        """{"error":{"type":"requests","code":"organization_spend_limit_exceeded","message":"Request refused."}}""",
        """{"error":{"type":"requests","code":"project_spend_limit_exceeded","message":"Request refused."}}""",
        """{"error":{"type":"requests","code":"organization_usage_limit_exceeded","message":"Request refused."}}""",
        """{"error":{"type":"invalid_request_error","code":null,"message":"insufficient_quota: add credits"}}""",
        """{"error":{"type":"credit_balance_exhausted","code":null,"message":"Request refused."}}""",
        """{"error":{"type":"requests","code":"INSUFFICIENT_QUOTA","message":"Request refused."}}""",
        """{"error":{"type":"requests","code":"invalid_value","message":"Request refused.","param":"insufficient_quota"}}""",
        // (28)–(30) Rate limits.
        """{"error":{"type":"requests","code":"rate_limit_exceeded","message":"Rate limit reached."}}""",
        """{"error":{"type":"requests","code":"slow_down","message":"Rate limit reached."}}""",
        """{"error":{"type":"rate_limit_error","code":null,"message":"Rate limit reached."}}""",
    ];

    /// <summary>The fixed message of each failure kind, the only text a failure may carry (R5).</summary>
    private static readonly Dictionary<AdvisorFailureKind, string> MESSAGES = new()
    {
        [AdvisorFailureKind.Unauthorized] = "Advisor authentication failed.",
        [AdvisorFailureKind.Forbidden] = "Advisor access was forbidden.",
        [AdvisorFailureKind.RateLimited] = "Advisor rate limit reached.",
        [AdvisorFailureKind.Overloaded] = "Advisor is overloaded.",
        [AdvisorFailureKind.TooLarge] = "Advisor request was too large.",
        [AdvisorFailureKind.Rejected] = "Advisor request was rejected.",
        [AdvisorFailureKind.TimedOut] = "Advisor request timed out.",
        [AdvisorFailureKind.Unavailable] = "Advisor is unavailable.",
        [AdvisorFailureKind.InvalidResponse] = "Advisor returned an invalid response.",
    };

    /// <summary>Every cell: a status and a one-based body number.</summary>
    public static TheoryData<int, int> Cells()
    {
        var data = new TheoryData<int, int>();
        foreach (var (status, body) in AllCells()) data.Add(status, body);
        return data;
    }

    // ---- The matrix ----

    /// <summary>One cell: the failure's kind, its fixed message, no inner exception, and the attempt count are exactly R5's.</summary>
    /// <param name="status">The status answered on every attempt.</param>
    /// <param name="body">The one-based number of the body answered on every attempt.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Every_cell_gets_its_expected_answer(int status, int body)
    {
        Assert.Empty(WIDER);
        var handler = new StubHandler(status, BODIES[body - 1]);
        using var advisor = new OpenAiDecisionsAdvisor("secret-openai-key", "gpt-6-luna", handler, (_, _) => Task.CompletedTask);

        var failure = await Assert.ThrowsAsync<AdvisorException>(() => advisor.AskAsync(Request(), CancellationToken.None));

        var (kind, attempts) = Expected(status, body);
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(MESSAGES[kind], failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal(attempts, handler.Attempts);
    }

    /// <summary>Every value of every axis occurs in some cell, and no cell is excluded.</summary>
    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        var cells = AllCells().ToList();
        Assert.Equal(STATUSES, cells.Select(cell => cell.Status).Distinct());
        Assert.Equal(Enumerable.Range(1, 30), cells.Select(cell => cell.Body).Distinct());
        Assert.Equal(30, BODIES.Length);
        Assert.Equal(570, cells.Count);
    }

    // ---- The expectation ----

    /// <summary>Every combination of the status and body axes.</summary>
    private static IEnumerable<(int Status, int Body)> AllCells() =>
        from status in STATUSES
        from body in Enumerable.Range(1, BODIES.Length)
        select (status, body);

    /// <summary>Written from R5, never from the code under test.</summary>
    /// <param name="status">The status answered on every attempt.</param>
    /// <param name="body">The one-based body number.</param>
    private static (AdvisorFailureKind Kind, int Attempts) Expected(int status, int body) => status switch
    {
        // R5: a 400 whose error code or message contains context_length or token, ignoring case, is too large: (10)–(15).
        400 when body is >= 10 and <= 15 => (AdvisorFailureKind.TooLarge, 1),
        // R5: any other 400 is rejected: unrecognised bodies, and markers only in type, another property or the top level.
        400 => (AdvisorFailureKind.Rejected, 1),
        // R5: 401 → unauthorized.
        401 => (AdvisorFailureKind.Unauthorized, 1),
        // R5: 403 → forbidden; 404 → forbidden.
        403 or 404 => (AdvisorFailureKind.Forbidden, 1),
        // R5: 408 → timed out.
        408 => (AdvisorFailureKind.TimedOut, 1),
        // R5: a 429 whose type is exactly insufficient_quota or whose code is exactly one of the five codes is forbidden,
        // never retried: (18)–(23); the message, another case, another type or another property never counts.
        429 when body is >= 18 and <= 23 => (AdvisorFailureKind.Forbidden, 1),
        // R5: any other 429, unrecognised bodies included, is rate limited and retried twice under today's policy.
        429 => (AdvisorFailureKind.RateLimited, 3),
        // R5: 503 → overloaded, retried twice under today's policy.
        503 => (AdvisorFailureKind.Overloaded, 3),
        // R5: every other 500–599 → unavailable.
        500 or 502 or 504 or 529 => (AdvisorFailureKind.Unavailable, 1),
        // R5: every other status → invalid response.
        101 or 204 or 302 or 405 or 409 or 413 or 415 or 422 => (AdvisorFailureKind.InvalidResponse, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No expectation was written for this status.")
    };

    /// <summary>The request every cell sends.</summary>
    private static AdviceRequest Request() =>
        new(new JsonObject { ["requirements"] = "context" }, new JsonObject { ["item"] = "one" },
            [new("relevance", "Does it apply?", [new("yes", "It applies"), new("no", "It does not")])]);

    /// <summary>A transport that answers every attempt with one status and body and counts the attempts.</summary>
    /// <param name="status">The status answered on every attempt.</param>
    /// <param name="body">The body answered on every attempt.</param>
    private sealed class StubHandler(int status, string body) : HttpMessageHandler
    {
        /// <summary>The number of attempts received.</summary>
        public int Attempts { get; private set; }

        /// <summary>Counts the attempt and answers it with a fresh response.</summary>
        /// <param name="request">The outgoing request.</param>
        /// <param name="cancellationToken">The timeout and caller cancellation token.</param>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
