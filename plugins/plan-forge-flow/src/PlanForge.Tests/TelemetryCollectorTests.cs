using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Orchestration;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The Telemetry collector (R9) on free ports: ingestion and admission, event time, usage answers,
/// lifetime, and the detached starter. Every collector a test starts is stopped by that test.
/// </summary>
public sealed partial class TelemetryCollectorTests
{
    private static readonly HttpClient HTTP = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly TimeSpan SHORT_GRACE = TimeSpan.FromSeconds(2);

    /// <summary>The test ports: below Windows' dynamic range, above the common service ports.</summary>
    private const int PORT_FIRST = 20000;
    private const int PORT_LAST = 39999;

    /// <summary>Every port <see cref="FreePort"/> has handed out in this process.</summary>
    private static readonly HashSet<int> HANDED_OUT = [];

    /// <summary>Where <see cref="FreePort"/> looks next; a random start per process. Guarded by <see cref="HANDED_OUT"/>.</summary>
    private static int _nextPort = Random.Shared.Next(PORT_FIRST, PORT_LAST + 1);

    // ---- The tests' own ports ----

    [Fact]
    public void FreePort_never_hands_out_one_port_twice_in_the_test_process()
    {
        // Every port FreePort released stays free for the system to hand out again; the binds between the
        // calls stand for the suite's own connections, and no number may come round twice to a test.
        var ports = new int[4000];
        Parallel.For(0, ports.Length, index =>
        {
            ports[index] = FreePort();
            for (var other = 0; other < 4; other++)
            {
                using var connection = new TcpListener(IPAddress.Loopback, 0);
                connection.Start();
            }
        });

        var repeated = ports.GroupBy(port => port).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        Assert.True(repeated.Count == 0, $"handed out more than once: {string.Join(", ", repeated)}");
    }

    [Fact]
    public async Task FreePort_hands_out_no_port_the_system_gives_client_sockets()
    {
        // A few hundred client connections held open, as the suite's HTTP clients hold theirs: their local
        // ports show the range the system hands to client sockets on this machine.
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start(1000);
        var endpoint = (IPEndPoint)server.LocalEndpoint;
        var clients = new List<TcpClient>();
        var accepted = new List<TcpClient>();
        try
        {
            for (var index = 0; index < 300; index++)
            {
                var client = new TcpClient(AddressFamily.InterNetwork);
                clients.Add(client);
                var accepting = server.AcceptTcpClientAsync();
                await client.ConnectAsync(endpoint.Address, endpoint.Port);
                accepted.Add(await accepting);
            }

            var clientPorts = clients.Select(client => ((IPEndPoint)client.Client.LocalEndPoint!).Port).ToHashSet();
            var lowest = clientPorts.Min();
            var ports = Enumerable.Range(0, 300).Select(_ => FreePort()).ToList();

            Assert.All(ports, port => Assert.DoesNotContain(port, clientPorts));
            Assert.All(ports, port => Assert.True(port < lowest, $"port {port} lies in the client range, which starts at or below {lowest}"));
        }
        finally
        {
            foreach (var client in clients.Concat(accepted)) client.Dispose();
        }
    }

    // ---- Requests the collector cannot parse (R9) ----

    [Fact]
    public async Task A_body_that_is_not_JSON_gets_400_and_changes_nothing()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        await PostAsync(collector, Envelope(ClaudeRequest(session, at)));
        var before = await UsageAsync(collector, session, at, at.AddMinutes(1));

        var answer = await HTTP.PostAsync(collector.Url("v1/logs"), new StringContent("this is not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        await AssertUnchangedAndAnsweringAsync(collector, session, at, before);
    }

    [Fact]
    public async Task A_body_that_is_not_valid_gzip_gets_400_and_changes_nothing()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        await PostAsync(collector, Envelope(ClaudeRequest(session, at)));
        var before = await UsageAsync(collector, session, at, at.AddMinutes(1));

        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Envelope(ClaudeRequest(session, at.AddSeconds(1))).ToJsonString()));
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        var answer = await HTTP.PostAsync(collector.Url("v1/logs"), content);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        await AssertUnchangedAndAnsweringAsync(collector, session, at, before);
    }

    [Fact]
    public async Task An_envelope_of_the_wrong_shape_gets_400_and_is_discarded_whole()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        await PostAsync(collector, Envelope(ClaudeRequest(session, at)));
        var before = await UsageAsync(collector, session, at, at.AddMinutes(1));

        // A valid record beside one that is not an object: the valid one is not kept either.
        var body = new JsonObject
        {
            ["resourceLogs"] = new JsonArray(new JsonObject
            {
                ["scopeLogs"] = new JsonArray(new JsonObject
                {
                    ["logRecords"] = new JsonArray(ClaudeRequest(session, at.AddSeconds(1)), 42),
                }),
            }),
        };
        var answer = await HTTP.PostAsync(collector.Url("v1/logs"), Json(body));
        var notAnArray = await HTTP.PostAsync(collector.Url("v1/logs"), Json(new JsonObject { ["resourceLogs"] = new JsonObject() }));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAnArray.StatusCode);
        await AssertUnchangedAndAnsweringAsync(collector, session, at, before);
    }

    [Theory]
    [InlineData("body that is a number")]
    [InlineData("attribute value that is not an object")]
    [InlineData("attribute without a string key")]
    [InlineData("logRecords that is not an array")]
    [InlineData("timeUnixNano that is an object")]
    [InlineData("observedTimeUnixNano that is a boolean")]
    public async Task A_record_of_the_wrong_shape_inside_the_envelope_gets_400_and_discards_the_request_whole(string shape)
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        await PostAsync(collector, Envelope(ClaudeRequest(session, at)));
        var before = await UsageAsync(collector, session, at, at.AddMinutes(1));

        // The wrong shape sits in an event the collector would keep, beside a valid one of the same session.
        var wrong = ClaudeRequest(session, at.AddSeconds(1));
        var records = new JsonArray(ClaudeRequest(session, at.AddSeconds(1)), wrong);
        JsonNode malformedScope = new JsonObject { ["logRecords"] = new JsonArray() };
        switch (shape)
        {
            case "body that is a number": wrong["body"] = 42; break;
            case "attribute value that is not an object":
                wrong["attributes"]!.AsArray().Add(new JsonObject { ["key"] = "model", ["value"] = "claude" });
                break;
            case "attribute without a string key":
                wrong["attributes"]!.AsArray().Add(new JsonObject { ["key"] = 7, ["value"] = Str("x") });
                break;
            case "logRecords that is not an array":
                records.Remove(wrong);
                malformedScope = new JsonObject { ["logRecords"] = new JsonObject() };
                break;
            case "timeUnixNano that is an object": wrong["timeUnixNano"] = new JsonObject { ["nanos"] = 1 }; break;
            case "observedTimeUnixNano that is a boolean": wrong["observedTimeUnixNano"] = true; break;
        }

        var body = new JsonObject
        {
            ["resourceLogs"] = new JsonArray(new JsonObject
            {
                ["scopeLogs"] = new JsonArray(new JsonObject { ["logRecords"] = records }, malformedScope),
            }),
        };
        var answer = await HTTP.PostAsync(collector.Url("v1/logs"), Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        await AssertUnchangedAndAnsweringAsync(collector, session, at, before);
    }

    [Fact]
    public async Task A_record_of_an_event_not_kept_is_shape_checked_too()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        await PostAsync(collector, Envelope(ClaudeRequest(session, at)));
        var before = await UsageAsync(collector, session, at, at.AddMinutes(1));

        var dropped = ClaudeRequest(Guid.NewGuid().ToString(), at, name: "claude_code.user_prompt");
        dropped["body"] = new JsonArray();
        var answer = await HTTP.PostAsync(collector.Url("v1/logs"), Json(Envelope(ClaudeRequest(session, at.AddSeconds(1)), dropped)));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        await AssertUnchangedAndAnsweringAsync(collector, session, at, before);
    }

    [Fact]
    public async Task A_null_body_or_time_reads_as_absent_not_as_the_wrong_shape()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        var record = CodexCompleted(session, at);
        record["body"] = null;
        record["timeUnixNano"] = null;

        await PostAsync(collector, Envelope(record));

        Assert.Equal(1, (await UsageAsync(collector, session, at, at.AddMinutes(1))).WindowEvents);
    }

    // ---- Totals of the recorded shapes (R7) ----

    [Fact]
    public async Task A_Claude_sample_of_the_recorded_shape_is_totalled_as_R7_says()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        // Counts as intValue numbers, as intValue decimal strings and as stringValue.
        await PostAsync(collector, Envelope(ClaudeRequest(session, at),
                                            ClaudeRequest(session, at.AddSeconds(1), input: Str("3"), cacheRead: IntText("10"),
                                                          cacheCreation: Int(5), output: Str("6"), cost: Dbl(0.5))));

        var usage = await UsageAsync(collector, session, at, at.AddMinutes(1));

        Assert.Equal(2, usage.WindowEvents);
        Assert.False(usage.Incomplete);
        Assert.Equal(new OrchestratorTokens(InputTokens: 2 + 46187 + 3 + 10 + 5, CacheReadTokens: 10, CacheCreationTokens: 46187 + 5,
                                            OutputTokens: 4 + 6, CostUsd: 0.0092396 + 0.5),
                     usage.Tokens);
    }

    [Fact]
    public async Task A_Codex_sample_of_the_recorded_shape_is_totalled_as_R7_says()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        await PostAsync(collector, Envelope(CodexCompleted(session, at),
                                            CodexCompleted(session, at.AddSeconds(1), input: Str("100"), cached: IntText("40"),
                                                           cacheWrite: Int(7), output: IntText("20"), reasoning: Str("9"))));

        var usage = await UsageAsync(collector, session, at, at.AddMinutes(1));

        // Codex's cached tokens are a subset of its input, so input is taken as given; no cost.
        Assert.Equal(2, usage.WindowEvents);
        Assert.Equal(new OrchestratorTokens(InputTokens: 1200 + 100, CacheReadTokens: 800 + 40, CacheCreationTokens: 0 + 7,
                                            OutputTokens: 300 + 20, ReasoningTokens: 100 + 9),
                     usage.Tokens);
    }

    [Theory]
    [InlineData("""{"boolValue":true}""")]
    [InlineData("""{"arrayValue":{"values":[{"intValue":7}]}}""")]
    [InlineData("""{"kvlistValue":{"values":[]}}""")]
    [InlineData("""{"bytesValue":"Bw=="}""")]
    [InlineData("{}")]
    public async Task A_counter_sent_as_another_value_kind_is_invalid_not_absent(string value)
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        await PostAsync(collector, Envelope(CodexCompleted(session, at, cached: (JsonObject)JsonNode.Parse(value)!),
                                            CodexCompleted(session, at.AddSeconds(1))));

        var usage = await UsageAsync(collector, session, at, at.AddMinutes(1));

        // The other event's 800 does not stand for the phase: the counter is absent, the record incomplete.
        Assert.True(usage.Incomplete);
        Assert.Equal(new OrchestratorTokens(InputTokens: 2400, CacheCreationTokens: 0, OutputTokens: 600, ReasoningTokens: 200),
                     usage.Tokens);
    }

    // ---- Event time (R7) ----

    [Fact]
    public Task Claude_event_time_is_event_timestamp_even_when_timeUnixNano_disagrees() =>
        AssertEventTimeAsync(Vendor.Claude, timestamp: true, time: TimeSpan.FromHours(1), observed: TimeSpan.FromHours(2), expected: TimeSpan.Zero);

    [Fact]
    public Task Claude_event_time_is_a_non_zero_timeUnixNano_without_event_timestamp() =>
        AssertEventTimeAsync(Vendor.Claude, timestamp: false, time: TimeSpan.FromHours(1), observed: TimeSpan.FromHours(2), expected: TimeSpan.FromHours(1));

    [Fact]
    public Task Claude_event_time_is_observedTimeUnixNano_when_timeUnixNano_is_zero() =>
        AssertEventTimeAsync(Vendor.Claude, timestamp: false, time: null, observed: TimeSpan.FromHours(2), expected: TimeSpan.FromHours(2));

    [Fact]
    public Task Codex_event_time_is_event_timestamp_even_when_timeUnixNano_disagrees() =>
        AssertEventTimeAsync(Vendor.Codex, timestamp: true, time: TimeSpan.FromHours(1), observed: TimeSpan.FromHours(2), expected: TimeSpan.Zero);

    [Fact]
    public Task Codex_event_time_is_a_non_zero_timeUnixNano_without_event_timestamp() =>
        AssertEventTimeAsync(Vendor.Codex, timestamp: false, time: TimeSpan.FromHours(1), observed: TimeSpan.FromHours(2), expected: TimeSpan.FromHours(1));

    [Fact]
    public Task Codex_event_time_is_observedTimeUnixNano_when_timeUnixNano_is_zero() =>
        AssertEventTimeAsync(Vendor.Codex, timestamp: false, time: null, observed: TimeSpan.FromHours(2), expected: TimeSpan.FromHours(2));

    [Fact]
    public async Task A_Claude_request_is_kept_at_its_start_event_timestamp_less_duration_ms()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        // event.timestamp = at + 1500 ms, duration_ms = 1500.
        await PostAsync(collector, Envelope(ClaudeRequest(session, at, durationMs: 1500)));

        Assert.Equal(1, (await UsageAsync(collector, session, at, at.AddMilliseconds(1500))).WindowEvents);
        Assert.Equal(0, (await UsageAsync(collector, session, at.AddMilliseconds(1), at.AddMinutes(1))).WindowEvents);
        Assert.Equal(0, (await UsageAsync(collector, session, at.AddMilliseconds(-1500), at)).WindowEvents);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("negative")]
    [InlineData("fractional")]
    [InlineData("non_digit_string")]
    [InlineData("boolean")]
    public async Task A_Claude_request_whose_duration_ms_is_not_a_valid_count_is_kept_at_event_timestamp(string duration)
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        (string, JsonObject)[] extra = duration switch
        {
            "absent" => [],
            "negative" => [("duration_ms", Int(-1500))],
            "fractional" => [("duration_ms", Dbl(1500.5))],
            "non_digit_string" => [("duration_ms", Str("1500ms"))],
            _ => [("duration_ms", new JsonObject { ["boolValue"] = true })],
        };

        await PostAsync(collector, Envelope(ClaudeRequest(session, at, durationMs: null, extra: extra)));

        Assert.Equal(1, (await UsageAsync(collector, session, at, at.AddMilliseconds(1))).WindowEvents);
        Assert.Equal(0, (await UsageAsync(collector, session, at.AddMinutes(-1), at)).WindowEvents);
    }

    [Fact]
    public async Task A_Claude_request_with_a_decimal_string_duration_ms_is_kept_at_its_start()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        var request = ClaudeRequest(session, at.AddMilliseconds(250), durationMs: null, extra: [("duration_ms", Str("250"))]);

        await PostAsync(collector, Envelope(request));

        Assert.Equal(1, (await UsageAsync(collector, session, at, at.AddMilliseconds(1))).WindowEvents);
    }

    [Fact]
    public async Task A_Codex_event_carrying_duration_ms_is_kept_at_event_timestamp()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        await PostAsync(collector, Envelope(CodexCompleted(session, at, extra: [("duration_ms", Int(1500))])));

        Assert.Equal(1, (await UsageAsync(collector, session, at, at.AddMilliseconds(1))).WindowEvents);
        Assert.Equal(0, (await UsageAsync(collector, session, at.AddMinutes(-1), at)).WindowEvents);
    }

    [Fact]
    public async Task A_Codex_response_takes_the_time_of_the_websocket_request_of_its_thread_before_it()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        await PostAsync(collector, Envelope(CodexRequestStart(session, at), CodexCompleted(session, at.AddSeconds(3))));

        Assert.Equal(1, (await UsageAsync(collector, session, at, at.AddMilliseconds(1))).WindowEvents);
        Assert.Equal(0, (await UsageAsync(collector, session, at.AddMilliseconds(1), at.AddMinutes(1))).WindowEvents);
        Assert.Equal(1, (await UsageAsync(collector, session, at.AddMinutes(-1), at.AddMinutes(1))).KeptEvents);
    }

    [Fact]
    public async Task A_Codex_response_takes_the_start_of_an_HTTP_api_request_of_its_thread_less_its_duration()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        // Stamped at + 2500 ms, at its end.
        await PostAsync(collector, Envelope(CodexRequestStart(session, at, "codex.api_request", 2500), CodexCompleted(session, at.AddSeconds(4))));

        Assert.Equal(1, (await UsageAsync(collector, session, at, at.AddMilliseconds(1))).WindowEvents);
    }

    [Fact]
    public async Task A_request_start_of_another_thread_is_ignored()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        var other = Guid.NewGuid().ToString();
        await RegisterAsync(collector.Port, other, Process.GetCurrentProcess());

        await PostAsync(collector, Envelope(CodexRequestStart(other, at), CodexCompleted(session, at.AddSeconds(3))));

        Assert.Equal(0, (await UsageAsync(collector, session, at, at.AddSeconds(3))).WindowEvents);
        Assert.Equal(1, (await UsageAsync(collector, session, at.AddSeconds(3), at.AddSeconds(4))).WindowEvents);
        Assert.Equal(0, (await UsageAsync(collector, other, at.AddMinutes(-1), at.AddMinutes(1))).KeptEvents);
    }

    [Fact]
    public async Task A_Codex_response_with_no_request_start_at_or_before_it_keeps_its_own_time()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        await PostAsync(collector, Envelope(CodexCompleted(session, at.AddSeconds(3)), CodexRequestStart(session, at.AddSeconds(5))));

        Assert.Equal(1, (await UsageAsync(collector, session, at.AddSeconds(3), at.AddSeconds(3).AddMilliseconds(1))).WindowEvents);
        Assert.Equal(0, (await UsageAsync(collector, session, at.AddSeconds(5), at.AddMinutes(1))).WindowEvents);
    }

    [Fact]
    public async Task Two_Codex_requests_in_a_row_pair_each_response_with_its_own_start()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        await PostAsync(collector, Envelope(CodexRequestStart(session, at), CodexCompleted(session, at.AddSeconds(2), input: Str("10")),
                                            CodexRequestStart(session, at.AddSeconds(3)), CodexCompleted(session, at.AddSeconds(5), input: Str("7"))));

        var first = await UsageAsync(collector, session, at, at.AddSeconds(3));
        var second = await UsageAsync(collector, session, at.AddSeconds(3), at.AddMinutes(1));
        Assert.Equal(1, first.WindowEvents);
        Assert.Equal(10, first.Tokens!.InputTokens);
        Assert.Equal(1, second.WindowEvents);
        Assert.Equal(7, second.Tokens!.InputTokens);
    }

    [Fact]
    public async Task A_request_start_arriving_in_a_later_export_than_its_response_still_pairs()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        await PostAsync(collector, Envelope(CodexCompleted(session, at.AddSeconds(3))));
        Assert.Equal(1, (await UsageAsync(collector, session, at.AddSeconds(3), at.AddSeconds(4))).WindowEvents);

        await PostAsync(collector, Envelope(CodexRequestStart(session, at)));
        Assert.Equal(1, (await UsageAsync(collector, session, at, at.AddMilliseconds(1))).WindowEvents);
        Assert.Equal(0, (await UsageAsync(collector, session, at.AddSeconds(3), at.AddSeconds(4))).WindowEvents);
    }

    [Fact]
    public async Task A_request_start_is_kept_as_its_time_alone_and_counts_as_no_event()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        await PostAsync(collector, Envelope(CodexRequestStart(session, at), CodexRequestStart(session, at.AddSeconds(1), "codex.api_request", 400)));

        Assert.Empty(collector.Collector.Kept());
        Assert.Equal([(session, at), (session, at.AddSeconds(1))], collector.Collector.KeptStarts());
        var usage = await UsageAsync(collector, session, at.AddMinutes(-1), at.AddMinutes(1));
        Assert.Equal(0, usage.KeptEvents);
        Assert.Equal(0, usage.WindowEvents);
        Assert.Null(usage.Tokens);
    }

    [Fact]
    public async Task A_Codex_response_whose_request_began_before_its_session_s_registration_is_not_counted()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        // The registration lies within the two seconds before `at`.
        await PostAsync(collector, Envelope(CodexRequestStart(session, at.AddSeconds(-3)), CodexCompleted(session, at.AddSeconds(1))));

        Assert.Equal(0, (await UsageAsync(collector, session, at.AddMinutes(-1), at.AddMinutes(1))).KeptEvents);
    }

    [Fact]
    public async Task A_Claude_request_that_began_before_its_session_s_registration_is_dropped_though_it_ended_after()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        // The registration lies within the two seconds before `at`: this request starts before it and ends after.
        await PostAsync(collector, Envelope(ClaudeRequest(session, at.AddSeconds(-3), durationMs: 3500)));

        Assert.Equal(0, (await UsageAsync(collector, session, at.AddMinutes(-1), at.AddMinutes(1))).KeptEvents);
    }

    // ---- Delivery and paths ----

    [Fact]
    public Task Delivery_works_through_127_0_0_1() => AssertDeliveryAsync("127.0.0.1");

    [Fact]
    public Task Delivery_works_through_localhost() => AssertDeliveryAsync("localhost");

    [Fact]
    public Task Delivery_works_through_the_IPv6_loopback() => AssertDeliveryAsync("[::1]");

    [Fact]
    public async Task A_gzip_body_is_taken()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        using var packed = new MemoryStream();
        await using (var gzip = new GZipStream(packed, CompressionMode.Compress, leaveOpen: true))
        {
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(Envelope(ClaudeRequest(session, at)).ToJsonString()));
        }

        var content = new ByteArrayContent(packed.ToArray());
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        var answer = await HTTP.PostAsync(collector.Url("v1/logs"), content);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(1, (await UsageAsync(collector, session, at, at.AddMinutes(1))).WindowEvents);
    }

    [Fact]
    public async Task Other_OTLP_paths_answer_success_and_are_discarded()
    {
        await using var collector = Running.Start();

        var metrics = await HTTP.PostAsync(collector.Url("v1/metrics"), Json(new JsonObject { ["resourceMetrics"] = new JsonArray() }));
        var traces = await HTTP.PostAsync(collector.Url("v1/traces"), new StringContent("not even json"));

        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.Equal("{}", await metrics.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, traces.StatusCode);
        Assert.Empty(collector.Collector.Kept());
    }

    [Fact]
    public async Task The_identity_answer_carries_the_protocol_version_process_id_and_start_time()
    {
        var before = DateTimeOffset.UtcNow;
        await using var collector = Running.Start();

        var identity = await IdentityAsync(collector.Port);

        Assert.Equal(TelemetryCollector.PROTOCOL, identity.Protocol);
        Assert.Equal(Environment.ProcessId, identity.ProcessId);
        Assert.InRange(identity.StartedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task The_usage_answer_tells_a_session_without_events_from_one_whose_events_lie_outside_the_window()
    {
        await using var collector = Running.Start();
        var (empty, at) = await RegisteredAsync(collector);
        var (outside, _) = await RegisteredAsync(collector);
        await PostAsync(collector, Envelope(ClaudeRequest(outside, at.AddHours(1))));

        var none = await UsageAsync(collector, empty, at, at.AddMinutes(1));
        var zero = await UsageAsync(collector, outside, at, at.AddMinutes(1));

        Assert.Equal(0, none.KeptEvents);
        Assert.Null(none.Tokens);
        Assert.Equal(1, zero.KeptEvents);
        Assert.Equal(0, zero.WindowEvents);
        Assert.Equal(new OrchestratorTokens(0, 0, 0, 0, null, 0), zero.Tokens);
    }

    [Fact]
    public async Task The_usage_of_a_session_the_collector_does_not_know_is_404()
    {
        await using var collector = Running.Start();
        var at = DateTimeOffset.UtcNow;

        var answer = await HTTP.GetAsync(collector.Url(UsageQuery("never-registered", at, at.AddMinutes(1))));

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [Fact]
    public async Task A_repeated_registration_keeps_the_first_registration_time()
    {
        await using var collector = Running.Start();
        var session = Guid.NewGuid().ToString();

        var first = await RegisterAsync(collector.Port, session, Process.GetCurrentProcess());
        await Task.Delay(50);
        var second = await RegisterAsync(collector.Port, session, Process.GetCurrentProcess());

        Assert.Equal(first, second);
    }

    // ---- Processes ----

    [Fact]
    public async Task A_second_collector_on_a_taken_port_exits_at_once()
    {
        await using var collector = Running.Start();

        using var second = Process.Start(new ProcessStartInfo(PlanForgeProgram, ["--telemetry-collector", "--port", Port(collector.Port)])
        {
            UseShellExecute = false, CreateNoWindow = true,
        })!;
        try
        {
            Assert.True(second.WaitForExit(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, second.ExitCode);
            Assert.Equal(Environment.ProcessId, (await IdentityAsync(collector.Port)).ProcessId);
        }
        finally
        {
            Kill(second);
        }
    }

    [Fact]
    public void The_relaunch_command_of_a_server_running_as_planforge_exe_is_the_program_itself()
    {
        var command = CollectorStarter.RelaunchCommand(@"C:\Plugins\plan-forge-flow\bin\planforge.exe", @"C:\Plugins\plan-forge-flow\bin\planforge.dll");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Plugins\plan-forge-flow\bin\planforge.exe", command.FileName);
        Assert.Empty(command.Arguments);
    }

    [Fact]
    public void The_relaunch_command_of_a_server_running_as_dotnet_planforge_dll_is_the_host_and_the_assembly()
    {
        var command = CollectorStarter.RelaunchCommand(@"C:\Program Files\dotnet\dotnet.exe", @"C:\Work\bin\planforge.dll");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", command.FileName);
        Assert.Equal([@"C:\Work\bin\planforge.dll"], command.Arguments);
        Assert.Null(CollectorStarter.RelaunchCommand(@"C:\Program Files\dotnet\dotnet.exe", @"C:\Work\bin\testhost.dll"));
        Assert.Null(CollectorStarter.RelaunchCommand(@"C:\Work\bin\testhost.exe", @"C:\Work\bin\planforge.dll"));
    }

    [Fact]
    public async Task A_helper_in_a_job_that_refuses_breakaway_and_kills_on_close_starts_a_collector_inside_it_that_ends_with_the_job()
    {
        var port = FreePort();
        var job = Job.Create(Job.KILL_ON_JOB_CLOSE);
        using var helper = StartHelper(job, port, PlanForgeProgram);
        int? collectorPid = null;
        try
        {
            var (outcome, pid) = await helper.StartCollectorAsync();
            collectorPid = pid;
            Assert.Equal(CollectorStartOutcome.Started, outcome);
            Assert.True(helper.InsideJob);
            Assert.Equal(pid, (await IdentityWithinAsync(port)).ProcessId);

            // Closing the job's last handle ends every process in it, the collector included.
            job.Dispose();
            Assert.True(helper.Process.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.True(ExitedWithin(pid!.Value, TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAsync<HttpRequestException>(() => HTTP.GetAsync(Url(port, "planforge/collector")));
        }
        finally
        {
            job.Dispose();
            KillById(collectorPid);
        }
    }

    [Fact]
    public async Task A_helper_in_a_job_that_permits_breakaway_starts_a_collector_that_outlives_the_job()
    {
        var port = FreePort();
        var job = Job.Create(Job.KILL_ON_JOB_CLOSE | Job.BREAKAWAY_OK);
        using var helper = StartHelper(job, port, PlanForgeProgram);
        int? collectorPid = null;
        try
        {
            var (outcome, pid) = await helper.StartCollectorAsync();
            collectorPid = pid;
            Assert.Equal(CollectorStartOutcome.Started, outcome);
            Assert.Equal(pid, (await IdentityWithinAsync(port)).ProcessId);

            // Closing the job kills the helper; the collector broke away from it.
            job.Dispose();
            Assert.True(helper.Process.WaitForExit(TimeSpan.FromSeconds(10)));
            await Task.Delay(500);
            Assert.Equal(pid, (await IdentityAsync(port)).ProcessId);
        }
        finally
        {
            job.Dispose();
            KillById(collectorPid);
        }
    }

    [Fact]
    public async Task A_collector_started_from_a_helper_that_then_exits_keeps_answering()
    {
        var port = FreePort();
        using var job = Job.Create(Job.BREAKAWAY_OK);
        using var helper = StartHelper(job, port, PlanForgeProgram);
        int? collectorPid = null;
        try
        {
            var (outcome, pid) = await helper.StartCollectorAsync();
            collectorPid = pid;
            Assert.Equal(CollectorStartOutcome.Started, outcome);

            // The collector inherits no handle of the helper: closing the helper's input ends it at once.
            helper.Process.StandardInput.Close();
            Assert.True(helper.Process.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.Equal(pid, (await IdentityWithinAsync(port)).ProcessId);
        }
        finally
        {
            KillById(collectorPid);
        }
    }

    [Fact]
    public async Task A_collector_process_started_with_the_dotnet_host_serves_its_port()
    {
        var port = FreePort();
        var command = new CollectorCommand(DotnetHost, [Path.Combine(AppContext.BaseDirectory, "planforge.dll")]);
        using var job = Job.Create(Job.BREAKAWAY_OK);
        using var helper = StartHelper(job, port, command.FileName, command.Arguments);
        int? collectorPid = null;
        try
        {
            var (outcome, pid) = await helper.StartCollectorAsync();
            collectorPid = pid;
            Assert.Equal(CollectorStartOutcome.Started, outcome);
            Assert.Equal(pid, (await IdentityWithinAsync(port)).ProcessId);
        }
        finally
        {
            KillById(collectorPid);
        }
    }

    // ---- Lifetime (R9), each answer checked before and after the grace ----

    [Fact]
    public async Task The_collector_ends_a_grace_after_its_one_registered_process_exits()
    {
        await using var collector = Running.Start(SHORT_GRACE);
        using var server = Waiter.Start();
        await RegisterAsync(collector.Port, Guid.NewGuid().ToString(), server.Process);

        await Task.Delay(SHORT_GRACE + TimeSpan.FromSeconds(1));
        await AssertAnsweringAsync(collector);

        server.Kill();
        await AssertAnsweringAsync(collector);
        await AssertEndedAsync(collector);
    }

    [Fact]
    public async Task The_collector_lives_while_one_of_two_registered_processes_is_alive()
    {
        await using var collector = Running.Start(SHORT_GRACE);
        using var first = Waiter.Start();
        using var second = Waiter.Start();
        await RegisterAsync(collector.Port, Guid.NewGuid().ToString(), first.Process);
        await RegisterAsync(collector.Port, Guid.NewGuid().ToString(), second.Process);

        first.Kill();
        await Task.Delay(SHORT_GRACE + TimeSpan.FromSeconds(1));
        await AssertAnsweringAsync(collector);

        second.Kill();
        await AssertAnsweringAsync(collector);
        await AssertEndedAsync(collector);
    }

    [Fact]
    public async Task Two_processes_registering_the_same_session_both_keep_the_collector_alive()
    {
        await using var collector = Running.Start(SHORT_GRACE);
        using var first = Waiter.Start();
        using var second = Waiter.Start();
        var session = Guid.NewGuid().ToString();
        var registeredAt = await RegisterAsync(collector.Port, session, first.Process);
        Assert.Equal(registeredAt, await RegisterAsync(collector.Port, session, second.Process));

        first.Kill();
        await Task.Delay(SHORT_GRACE + TimeSpan.FromSeconds(1));
        await AssertAnsweringAsync(collector);

        second.Kill();
        await AssertAnsweringAsync(collector);
        await AssertEndedAsync(collector);
    }

    [Fact]
    public async Task A_process_id_reused_by_another_process_does_not_keep_the_collector_alive()
    {
        await using var collector = Running.Start(SHORT_GRACE);
        using var other = Waiter.Start();

        // The id is alive, but its start time is not the registered process's: another process.
        var registration = new SessionRegistration(Guid.NewGuid().ToString(), other.Process.Id,
                                                   TelemetryCollector.ProcessStartTime(other.Process).AddMinutes(-5));
        var answer = await HTTP.PostAsync(collector.Url("planforge/sessions"),
                                          Json(JsonSerializer.Serialize(registration, CollectorJson.Default.SessionRegistration)));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

        await AssertAnsweringAsync(collector);
        await AssertEndedAsync(collector);
        Assert.False(other.Process.HasExited);
    }

    [Fact]
    public async Task A_collector_nothing_registers_with_ends_a_grace_after_starting()
    {
        await using var collector = Running.Start(SHORT_GRACE);

        await AssertAnsweringAsync(collector);
        await AssertEndedAsync(collector);
    }

    [Fact]
    public async Task A_registration_during_the_grace_keeps_the_collector_alive()
    {
        // The server process starts first and registers at once: on a slow runner its start alone can
        // outlast the grace, which runs from the collector's start.
        using var server = Waiter.Start();
        await using var collector = Running.Start(SHORT_GRACE);

        await RegisterAsync(collector.Port, Guid.NewGuid().ToString(), server.Process);
        await Task.Delay(SHORT_GRACE + TimeSpan.FromSeconds(1));
        await AssertAnsweringAsync(collector);

        server.Kill();
        await AssertAnsweringAsync(collector);
        await AssertEndedAsync(collector);
    }

    // ---- Admission (R9) ----

    [Theory]
    [InlineData("127.0.0.1", true, "claude_code.api_request", null, true)]
    [InlineData("::1", true, "claude_code.api_request", null, true)]
    [InlineData("::ffff:127.0.0.1", true, "claude_code.api_request", null, true)]
    [InlineData("192.168.1.20", true, "claude_code.api_request", null, false)]
    [InlineData("10.0.0.5", true, "codex.sse_event", "response.completed", false)]
    [InlineData("127.0.0.1", false, "claude_code.api_request", null, false)]
    [InlineData("127.0.0.1", false, "codex.sse_event", "response.completed", false)]
    [InlineData("127.0.0.1", true, "claude_code.user_prompt", null, false)]
    [InlineData("127.0.0.1", true, "claude_code.assistant_response", null, false)]
    [InlineData("127.0.0.1", true, "claude_code.plugin_loaded", null, false)]
    [InlineData("127.0.0.1", true, "codex.sse_event", "response.completed", true)]
    [InlineData("127.0.0.1", true, "codex.sse_event", "response.output_item.done", false)]
    [InlineData("127.0.0.1", true, "codex.sse_event", null, false)]
    [InlineData("127.0.0.1", true, "codex.api_request", null, false)]
    [InlineData("127.0.0.1", true, null, null, false)]
    public void Admission_is_one_predicate_over_caller_registration_name_and_kind(string caller, bool registered, string? name, string? kind,
                                                                                  bool admitted) =>
        Assert.Equal(admitted, TelemetryCollector.Admits(IPAddress.Parse(caller), registered, name, kind));

    [Fact]
    public async Task Only_a_registered_session_s_API_request_events_are_kept()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        var (codex, _) = await RegisteredAsync(collector);
        var unregistered = Guid.NewGuid().ToString();

        await PostAsync(collector, Envelope(ClaudeRequest(session, at, name: "claude_code.user_prompt"),
                                            ClaudeRequest(session, at, name: "claude_code.assistant_response"),
                                            ClaudeRequest(unregistered, at),
                                            CodexCompleted(codex, at, kind: "response.created"),
                                            CodexCompleted(unregistered, at)));

        Assert.Empty(collector.Collector.Kept());
        Assert.Equal(0, (await UsageAsync(collector, session, at, at.AddMinutes(1))).KeptEvents);
    }

    [Fact]
    public async Task API_requests_of_every_query_source_are_kept()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        await PostAsync(collector, Envelope([.. new[] { "sdk", "main", "subagent", "compact" }.Select(source =>
            ClaudeRequest(session, at, extra: [("query_source", Str(source))]))]));

        Assert.Equal(4, (await UsageAsync(collector, session, at, at.AddMinutes(1))).WindowEvents);
    }

    [Fact]
    public async Task The_kept_data_holds_no_prompt_e_mail_account_user_or_organization_value()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        (string, JsonObject)[] identity =
        [
            ("prompt", Str("SECRET-PROMPT")), ("user.email", Str("SECRET-EMAIL@example.com")),
            ("user.account_uuid", Str("SECRET-ACCOUNT")), ("user.id", Str("SECRET-USER")),
            ("organization.id", Str("SECRET-ORG")), ("prompt.id", Str("SECRET-PROMPT-ID")),
        ];

        await PostAsync(collector, Envelope(ClaudeRequest(session, at, extra: identity),
                                            ClaudeRequest(session, at, name: "claude_code.user_prompt", extra: identity),
                                            CodexCompleted(session, at, extra: identity)));

        var kept = collector.Collector.Kept();
        Assert.Equal(2, kept.Count);
        string[] usageFields = ["input_tokens", "cache_read_tokens", "cache_creation_tokens", "output_tokens", "cost_usd",
                                "input_token_count", "cached_token_count", "cache_write_token_count", "output_token_count",
                                "reasoning_token_count"];
        Assert.All(kept, e => Assert.All(e.Usage.Keys, key => Assert.Contains(key, usageFields)));
        Assert.DoesNotContain(kept.SelectMany(e => e.Usage.Values), value => value.GetRawText().Contains("SECRET", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"stringValue":"SECRET-PROMPT the user typed, long and not digits at all"}""")]
    [InlineData("""{"intValue":{"prompt":"SECRET-PROMPT"}}""")]
    [InlineData("""{"intValue":"SECRET-EMAIL@example.com"}""")]
    [InlineData("""{"doubleValue":"SECRET-ACCOUNT"}""")]
    [InlineData("""{"kvlistValue":{"values":[{"key":"SECRET-KEY","value":{"stringValue":"SECRET-ORG"}}]}}""")]
    [InlineData("""{"arrayValue":{"values":[{"stringValue":"SECRET-USER"}]}}""")]
    public async Task A_counter_carrying_other_content_is_kept_only_as_an_invalid_marker(string value)
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        var (thread, _) = await RegisteredAsync(collector);
        var sent = (JsonObject)JsonNode.Parse(value)!;

        await PostAsync(collector, Envelope(ClaudeRequest(session, at, cacheRead: sent, cost: (JsonObject)sent.DeepClone()),
                                            CodexCompleted(thread, at, cached: (JsonObject)sent.DeepClone())));

        var kept = collector.Collector.Kept();
        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept.SelectMany(e => e.Usage.Values), v => v.GetRawText().Contains("SECRET", StringComparison.Ordinal));
        Assert.All(kept.SelectMany(e => e.Usage.Values), v => Assert.Contains(v.ValueKind, new[] { JsonValueKind.Number, JsonValueKind.Null }));

        // The reading marks the counters invalid (R13): absent, and the record incomplete; Claude's input
        // cannot be formed without a valid cache-read component.
        var claude = await UsageAsync(collector, session, at, at.AddMinutes(1));
        var codex = await UsageAsync(collector, thread, at, at.AddMinutes(1));
        Assert.True(claude.Incomplete);
        Assert.Equal(new OrchestratorTokens(CacheCreationTokens: 46187, OutputTokens: 4), claude.Tokens);
        Assert.True(codex.Incomplete);
        Assert.Equal(new OrchestratorTokens(InputTokens: 1200, CacheCreationTokens: 0, OutputTokens: 300, ReasoningTokens: 100), codex.Tokens);
    }

    [Fact]
    public async Task Valid_counters_are_kept_as_the_numbers_they_stand_for()
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        await PostAsync(collector, Envelope(ClaudeRequest(session, at, input: Str("0003"), cacheRead: IntText("10"), cost: Dbl(0.5))));

        var usage = Assert.Single(collector.Collector.Kept()).Usage;
        Assert.Equal(3, usage["input_tokens"].GetInt64());
        Assert.Equal(10, usage["cache_read_tokens"].GetInt64());
        Assert.Equal(46187, usage["cache_creation_tokens"].GetInt64());
        Assert.Equal(0.5, usage["cost_usd"].GetDouble());
        Assert.All(usage.Values, v => Assert.Equal(JsonValueKind.Number, v.ValueKind));
    }

    // ---- Helpers ----

    private enum Vendor { Claude, Codex }

    internal static string PlanForgeProgram => Path.Combine(AppContext.BaseDirectory, "planforge.exe");

    /// <summary>The dotnet host running this test process, from the shared runtime directory it loaded.</summary>
    internal static string DotnetHost =>
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));

    private static async Task AssertEventTimeAsync(Vendor vendor, bool timestamp, TimeSpan? time, TimeSpan observed, TimeSpan expected)
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);
        // R7's order alone: a Claude request without a duration has no start to move back to.
        var record = vendor == Vendor.Claude ? ClaudeRequest(session, timestamp ? at : null, durationMs: null) : CodexCompleted(session, timestamp ? at : null);
        record["timeUnixNano"] = time is { } t ? Nanos(at + t) : "0";
        record["observedTimeUnixNano"] = Nanos(at + observed);

        await PostAsync(collector, Envelope(record));

        foreach (var offset in new[] { TimeSpan.Zero, TimeSpan.FromHours(1), TimeSpan.FromHours(2) })
        {
            var usage = await UsageAsync(collector, session, at + offset, at + offset + TimeSpan.FromMinutes(1));
            Assert.Equal(offset == expected ? 1 : 0, usage.WindowEvents);
        }
    }

    private static async Task AssertDeliveryAsync(string host)
    {
        await using var collector = Running.Start();
        var (session, at) = await RegisteredAsync(collector);

        var answer = await HTTP.PostAsync(Url(collector.Port, "v1/logs", host), Json(Envelope(ClaudeRequest(session, at))));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(1, (await UsageAsync(collector, session, at, at.AddMinutes(1))).WindowEvents);
    }

    private static async Task AssertUnchangedAndAnsweringAsync(Running collector, string session, DateTimeOffset at, CollectorUsage before)
    {
        Assert.Equal(before, await UsageAsync(collector, session, at, at.AddMinutes(1)));
        await AssertAnsweringAsync(collector);
        await PostAsync(collector, Envelope(ClaudeRequest(session, at.AddSeconds(2))));
        Assert.Equal(before.KeptEvents + 1, (await UsageAsync(collector, session, at, at.AddMinutes(1))).KeptEvents);
    }

    private static async Task AssertAnsweringAsync(Running collector)
    {
        Assert.False(collector.Run.IsCompleted);
        Assert.Equal(TelemetryCollector.PROTOCOL, (await IdentityAsync(collector.Port)).Protocol);
    }

    private static async Task AssertEndedAsync(Running collector)
    {
        await collector.Run.WaitAsync(SHORT_GRACE + TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HttpRequestException>(() => HTTP.GetAsync(collector.Url("planforge/collector")));
    }

    /// <summary>
    /// A fresh session registered for this test process, and a whole second after its registration,
    /// so that an <c>event.timestamp</c> written to the millisecond lands on it exactly.
    /// </summary>
    /// <param name="collector">The collector to register with.</param>
    private static async Task<(string Session, DateTimeOffset At)> RegisteredAsync(Running collector)
    {
        var session = Guid.NewGuid().ToString();
        var registeredAt = await RegisterAsync(collector.Port, session, Process.GetCurrentProcess());
        var ticks = registeredAt.UtcTicks;
        return (session, new DateTimeOffset(ticks - ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero).AddSeconds(2));
    }

    private static async Task<DateTimeOffset> RegisterAsync(int port, string session, Process process)
    {
        var registration = new SessionRegistration(session, process.Id, TelemetryCollector.ProcessStartTime(process));
        var answer = await HTTP.PostAsync(Url(port, "planforge/sessions"),
                                          Json(JsonSerializer.Serialize(registration, CollectorJson.Default.SessionRegistration)));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return JsonSerializer.Deserialize(await answer.Content.ReadAsStringAsync(), CollectorJson.Default.RegistrationAnswer)!.RegisteredAt;
    }

    private static async Task<CollectorUsage> UsageAsync(Running collector, string session, DateTimeOffset from, DateTimeOffset to)
    {
        var answer = await HTTP.GetAsync(collector.Url(UsageQuery(session, from, to)));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return JsonSerializer.Deserialize(await answer.Content.ReadAsStringAsync(), CollectorJson.Default.CollectorUsage)!;
    }

    private static string UsageQuery(string session, DateTimeOffset from, DateTimeOffset to) =>
        $"planforge/usage?session={Uri.EscapeDataString(session)}&from={Uri.EscapeDataString(from.ToString("o"))}"
        + $"&to={Uri.EscapeDataString(to.ToString("o"))}";

    internal static async Task<CollectorIdentity> IdentityAsync(int port)
    {
        var answer = await HTTP.GetAsync(Url(port, "planforge/collector"));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return JsonSerializer.Deserialize(await answer.Content.ReadAsStringAsync(), CollectorJson.Default.CollectorIdentity)!;
    }

    /// <summary>The identity of a collector process that may still be binding its port.</summary>
    /// <param name="port">The collector's port.</param>
    internal static async Task<CollectorIdentity> IdentityWithinAsync(int port)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                return await IdentityAsync(port);
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    private static async Task PostAsync(Running collector, JsonObject envelope)
    {
        var answer = await HTTP.PostAsync(collector.Url("v1/logs"), Json(envelope));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
    }

    private static StringContent Json(JsonNode body) => Json(body.ToJsonString());

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Url(int port, string path, string host = "127.0.0.1") => $"http://{host}:{port}/{path}";

    private static string Port(int port) => port.ToString(CultureInfo.InvariantCulture);

    private static string Nanos(DateTimeOffset at) =>
        ((ulong)(at - DateTimeOffset.UnixEpoch).Ticks * 100).ToString(CultureInfo.InvariantCulture);

    private static JsonObject Str(string value) => new() { ["stringValue"] = value };

    private static JsonObject Int(long value) => new() { ["intValue"] = value };

    private static JsonObject IntText(string value) => new() { ["intValue"] = value };

    private static JsonObject Dbl(double value) => new() { ["doubleValue"] = value };

    internal static JsonObject Envelope(params JsonObject[] records) => new()
    {
        ["resourceLogs"] = new JsonArray(new JsonObject
        {
            ["resource"] = new JsonObject { ["attributes"] = new JsonArray() },
            ["scopeLogs"] = new JsonArray(new JsonObject
            {
                ["scope"] = new JsonObject { ["name"] = "com.anthropic.claude_code.events" },
                ["logRecords"] = new JsonArray([.. records]),
            }),
        }),
    };

    // A claude_code.api_request of the recorded shape: input 2, cache creation 46187, output 4. Claude Code stamps
    // it as the response ends, so `at` is the request's start, and the event's times are `at` plus `durationMs`;
    // a null `durationMs` sends no duration_ms, and the times are `at` itself.
    internal static JsonObject ClaudeRequest(string session, DateTimeOffset? at, string name = "claude_code.api_request",
                                            JsonObject? input = null, JsonObject? cacheRead = null, JsonObject? cacheCreation = null,
                                            JsonObject? output = null, JsonObject? cost = null, (string, JsonObject)[]? extra = null,
                                            long? durationMs = 1200)
    {
        List<(string, JsonObject)> attributes =
        [
            ("event.name", Str(name["claude_code.".Length..])), ("session.id", Str(session)), ("model", Str("claude-opus-5-5")),
            ("input_tokens", input ?? Int(2)), ("output_tokens", output ?? Int(4)), ("cache_read_tokens", cacheRead ?? Int(0)),
            ("cache_creation_tokens", cacheCreation ?? Int(46187)), ("cost_usd", cost ?? Dbl(0.0092396)),
            ("query_source", Str("main")),
        ];
        if (durationMs is { } duration) attributes.Add(("duration_ms", Int(duration)));
        var end = at?.AddMilliseconds(durationMs ?? 0);
        if (end is { } time) attributes.Add(("event.timestamp", Str(time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))));
        attributes.AddRange(extra ?? []);
        return Record(new JsonObject { ["stringValue"] = name }, end is { } t ? Nanos(t) : Nanos(DateTimeOffset.UtcNow), attributes);
    }

    // A Codex codex.sse_event of the recorded shape: no body, timeUnixNano 0, counts as decimal strings and integers.
    internal static JsonObject CodexCompleted(string thread, DateTimeOffset? at, string kind = "response.completed",
                                             JsonObject? input = null, JsonObject? cached = null, JsonObject? cacheWrite = null,
                                             JsonObject? output = null, JsonObject? reasoning = null, (string, JsonObject)[]? extra = null)
    {
        List<(string, JsonObject)> attributes =
        [
            ("event.name", Str("codex.sse_event")), ("event.kind", Str(kind)), ("conversation.id", Str(thread)), ("model", Str("gpt-5.5")),
            ("input_token_count", input ?? Str("1200")), ("output_token_count", output ?? Str("300")),
            ("cached_token_count", cached ?? Int(800)), ("cache_write_token_count", cacheWrite ?? Int(0)),
            ("reasoning_token_count", reasoning ?? Int(100)), ("tool_token_count", Int(0)),
        ];
        if (at is { } time) attributes.Add(("event.timestamp", Str(time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))));
        attributes.AddRange(extra ?? []);
        var record = Record(null, "0", attributes);
        record["observedTimeUnixNano"] = Nanos(at ?? DateTimeOffset.UtcNow);
        return record;
    }

    // A Codex request start as recorded: codex.websocket_request at `at`, its duration_ms (~1 ms) the send only;
    // or, for the HTTP transport, codex.api_request starting at `at` and stamped `durationMs` later, at its end.
    internal static JsonObject CodexRequestStart(string thread, DateTimeOffset at, string name = "codex.websocket_request", long durationMs = 1,
                                                 (string, JsonObject)[]? extra = null)
    {
        var end = name == "codex.api_request" ? at.AddMilliseconds(durationMs) : at;
        List<(string, JsonObject)> attributes =
        [
            ("event.name", Str(name)), ("conversation.id", Str(thread)), ("model", Str("gpt-5.5")),
            ("auth_mode", Str("chatgpt")), ("user.email", Str("someone@example.invalid")), ("duration_ms", Int(durationMs)),
            ("event.timestamp", Str(end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))),
        ];
        attributes.AddRange(extra ?? []);
        var record = Record(null, "0", attributes);
        record["observedTimeUnixNano"] = Nanos(end);
        return record;
    }

    private static JsonObject Record(JsonObject? body, string timeNanos, IEnumerable<(string Key, JsonObject Value)> attributes)
    {
        var record = new JsonObject
        {
            ["timeUnixNano"] = timeNanos,
            ["observedTimeUnixNano"] = timeNanos,
            ["attributes"] = new JsonArray([.. attributes.Select(a => (JsonNode)new JsonObject { ["key"] = a.Key, ["value"] = a.Value.DeepClone() })]),
        };
        if (body is not null) record["body"] = body;
        return record;
    }

    /// <summary>
    /// A loopback port free now on both <c>127.0.0.1</c> and <c>[::1]</c>, and never handed out before in
    /// this test process. It comes from <see cref="PORT_FIRST"/>–<see cref="PORT_LAST"/>, below Windows'
    /// dynamic range (49152 on): a port from that range can be taken as the local port of one of the
    /// suite's own client connections between its release here and the test's bind, and http.sys then
    /// refuses the prefix. The walk starts at a random point per process, so parallel test processes
    /// rarely meet; a port either bind refuses — in use, or reserved by Hyper-V or WinNAT — is skipped.
    /// </summary>
    internal static int FreePort()
    {
        lock (HANDED_OUT)
        {
            const int Count = PORT_LAST - PORT_FIRST + 1;
            for (var step = 0; step < Count; step++)
            {
                var port = PORT_FIRST + (_nextPort - PORT_FIRST + step) % Count;
                if (HANDED_OUT.Contains(port) || !Bindable(IPAddress.Loopback, port)
                    || Socket.OSSupportsIPv6 && !Bindable(IPAddress.IPv6Loopback, port))
                {
                    continue;
                }

                HANDED_OUT.Add(port);
                _nextPort = PORT_FIRST + (port - PORT_FIRST + 1) % Count;
                return port;
            }
        }

        throw new InvalidOperationException("no loopback port was left that this test process had not handed out");
    }

    /// <summary>Whether an exclusive listener can bind the port on the address now.</summary>
    /// <param name="address">The loopback address.</param>
    /// <param name="port">The port.</param>
    private static bool Bindable(IPAddress address, int port)
    {
        try
        {
            using var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Whether the process has exited, or exits within the timeout; a process already gone has.</summary>
    /// <param name="pid">The process id.</param>
    /// <param name="timeout">How long to wait.</param>
    private static bool ExitedWithin(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    internal static void KillById(int? pid)
    {
        if (pid is not { } id) return;
        try
        {
            using var process = Process.GetProcessById(id);
            Kill(process);
        }
        catch (ArgumentException)
        {
        }
    }

    internal static Helper StartHelper(Job job, int port, string program, IReadOnlyList<string>? arguments = null)
    {
        var info = new ProcessStartInfo(DotnetHost)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "exec", typeof(TestHelperProgram).Assembly.Location, "start-collector", Port(port), "30", program })
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments ?? []) info.ArgumentList.Add(argument);
        var process = Process.Start(info)!;
        job.Assign(process);
        return new Helper(process);
    }

    /// <summary>A helper process that starts a collector through the starter once it is told to.</summary>
    /// <param name="process">The helper process, its standard input and output redirected.</param>
    internal sealed class Helper(Process process) : IDisposable
    {
        public Process Process { get; } = process;

        /// <summary>Whether the last collector started runs inside the helper's job object.</summary>
        public bool InsideJob { get; private set; }

        public async Task<(CollectorStartOutcome Outcome, int? Pid)> StartCollectorAsync()
        {
            await Process.StandardInput.WriteLineAsync("go");
            await Process.StandardInput.FlushAsync();
            var line = await Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(line);
            var parts = line.Split(' ');
            InsideJob = parts.Length > 2 && bool.Parse(parts[2]);
            return (Enum.Parse<CollectorStartOutcome>(parts[0]),
                    parts.Length > 1 && parts[1].Length > 0 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : null);
        }

        public void Dispose()
        {
            Kill(Process);
            Process.Dispose();
        }
    }

    /// <summary>A process that lives until killed, to register with a collector as a server would.</summary>
    private sealed class Waiter : IDisposable
    {
        private Waiter(Process process) => Process = process;

        public Process Process { get; }

        public static Waiter Start()
        {
            var info = new ProcessStartInfo(DotnetHost) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            info.ArgumentList.Add("exec");
            info.ArgumentList.Add(typeof(TestHelperProgram).Assembly.Location);
            info.ArgumentList.Add("wait");
            return new Waiter(Process.Start(info)!);
        }

        public void Kill()
        {
            TelemetryCollectorTests.Kill(Process);
            Process.WaitForExit();
        }

        public void Dispose()
        {
            TelemetryCollectorTests.Kill(Process);
            Process.Dispose();
        }
    }

    /// <summary>
    /// System time whose timers fire fifteen times later, for a test that talks to a real collector but
    /// is not about a deadline: on a loaded CI runner one loopback request can outlast R10's 2 seconds.
    /// </summary>
    internal sealed class SlowTimers : TimeProvider
    {
        private const int STRETCH = 15;

        public static SlowTimers Instance { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Stretched(TimeProvider.System.CreateTimer(callback, state, Stretch(dueTime), Stretch(period)));

        internal static TimeSpan Stretch(TimeSpan span) =>
            span == Timeout.InfiniteTimeSpan || span <= TimeSpan.Zero ? span : span * STRETCH;

        private sealed class Stretched(ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(Stretch(dueTime), Stretch(period));

            public void Dispose() => inner.Dispose();

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    /// <summary>An in-process collector on a free port, stopped when disposed.</summary>
    internal sealed class Running : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        private Running(TelemetryCollector collector, int port)
        {
            Collector = collector;
            Port = port;
            Run = collector.RunAsync(_stop.Token);
        }

        public TelemetryCollector Collector { get; }

        public int Port { get; }

        public Task Run { get; }

        public static Running Start(TimeSpan? grace = null, int? port = null)
        {
            // A port of the caller's own is kept as given; one chosen here may be taken by a process
            // outside the test run, and then another is chosen, a few times at most.
            for (var attempt = 1; ; attempt++)
            {
                var chosen = port ?? FreePort();
                var collector = new TelemetryCollector(chosen, grace ?? TimeSpan.FromMinutes(5));
                if (collector.TryStart()) return new Running(collector, chosen);

                collector.Dispose();
                Assert.True(port is null && attempt < 5, $"no collector could start on port {chosen}");
            }
        }

        public string Url(string path) => TelemetryCollectorTests.Url(Port, path);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Run;
            Collector.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>A job object for a helper process, closed when disposed.</summary>
    internal sealed partial class Job : IDisposable
    {
        public const int BREAKAWAY_OK = 0x00000800;
        public const int KILL_ON_JOB_CLOSE = 0x00002000;
        private const int EXTENDED_LIMIT_INFORMATION = 9;

        private nint _handle;

        private Job(nint handle) => _handle = handle;

        public static Job Create(int limitFlags)
        {
            var handle = CreateJobObject(0, null);
            Assert.NotEqual(0, handle);
            var limits = new ExtendedLimits { LimitFlags = limitFlags };
            Assert.True(SetInformationJobObject(handle, EXTENDED_LIMIT_INFORMATION, in limits, Marshal.SizeOf<ExtendedLimits>()));
            return new Job(handle);
        }

        public void Assign(Process process) => Assert.True(AssignProcessToJobObject(_handle, process.Handle));

        public void Dispose()
        {
            if (_handle == 0) return;
            CloseHandle(_handle);
            _handle = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public int LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public int ActiveProcessLimit;
            public nuint Affinity;
            public int PriorityClass;
            public int SchedulingClass;
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint CreateJobObject(nint attributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(nint job, int informationClass, in ExtendedLimits information, int length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(nint job, nint process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
