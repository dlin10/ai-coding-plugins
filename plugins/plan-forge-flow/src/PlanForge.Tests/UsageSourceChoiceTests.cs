using System.Text.Json.Nodes;
using PlanForge.Orchestration;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// R6's profile reader: which Claude Code <c>settings.json</c> and Codex <c>config.toml</c> configure the
/// telemetry export, and Claude's export interval. Every profile here is a string the test writes; no
/// test reads a real profile.
/// </summary>
public sealed class UsageSourceChoiceTests
{
    private static readonly string[] CLAUDE_KEYS =
        ["CLAUDE_CODE_ENABLE_TELEMETRY", "OTEL_LOGS_EXPORTER", "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"];

    // ---- Claude Code ----

    [Fact]
    public void A_Claude_profile_with_every_key_set_configures_the_export()
    {
        var verdict = ExportProfile.Claude(Settings());

        Assert.Equal(new ExportEndpoint("127.0.0.1", 4318, TimeSpan.FromSeconds(5)), verdict.Endpoint);
    }

    [Fact]
    public void Each_Claude_key_absent_leaves_the_export_unconfigured()
    {
        foreach (var key in CLAUDE_KEYS)
            Assert.Null(ExportProfile.Claude(Settings(env => env.Remove(key))).Endpoint);
    }

    [Fact]
    public void Each_Claude_key_with_a_wrong_value_leaves_the_export_unconfigured()
    {
        (string Key, string Value)[] wrong =
        [
            ("CLAUDE_CODE_ENABLE_TELEMETRY", "0"), ("CLAUDE_CODE_ENABLE_TELEMETRY", "yes"), ("OTEL_LOGS_EXPORTER", "console"),
            ("OTEL_LOGS_EXPORTER", "otlpx"), ("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc"), ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            ("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "https://127.0.0.1:4318/v1/logs"), ("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "not a url"),
        ];

        foreach (var (key, value) in wrong)
            Assert.Null(ExportProfile.Claude(Settings(env => env[key] = value)).Endpoint);
    }

    [Fact]
    public void A_Claude_key_that_is_not_a_string_counts_as_absent()
    {
        foreach (var key in CLAUDE_KEYS)
            Assert.Null(ExportProfile.Claude(Settings(env => env[key] = 1)).Endpoint);

        // Absent, the logs protocol falls back to the generic one, which is right here.
        Assert.NotNull(ExportProfile.Claude(Settings(env =>
        {
            env["OTEL_EXPORTER_OTLP_LOGS_PROTOCOL"] = true;
        })).Endpoint);
    }

    [Fact]
    public void Enable_telemetry_is_true_in_any_case_or_1()
    {
        Assert.NotNull(ExportProfile.Claude(Settings(env => env["CLAUDE_CODE_ENABLE_TELEMETRY"] = "TRUE")).Endpoint);
        Assert.NotNull(ExportProfile.Claude(Settings(env => env["CLAUDE_CODE_ENABLE_TELEMETRY"] = "True")).Endpoint);
        Assert.NotNull(ExportProfile.Claude(Settings(env => env["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1")).Endpoint);
    }

    [Fact]
    public void The_exporter_list_is_a_comma_list_containing_otlp()
    {
        Assert.NotNull(ExportProfile.Claude(Settings(env => env["OTEL_LOGS_EXPORTER"] = "console, otlp")).Endpoint);
    }

    [Fact]
    public void The_logs_protocol_overrides_the_generic_one()
    {
        Assert.NotNull(ExportProfile.Claude(Settings(env =>
        {
            env["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc";
            env["OTEL_EXPORTER_OTLP_LOGS_PROTOCOL"] = "http/json";
        })).Endpoint);
        Assert.Null(ExportProfile.Claude(Settings(env => env["OTEL_EXPORTER_OTLP_LOGS_PROTOCOL"] = "grpc")).Endpoint);
    }

    [Fact]
    public void An_endpoint_on_a_host_that_is_not_loopback_is_not_configured()
    {
        Assert.Null(ExportProfile.Claude(Settings(env => env["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://collector.example.com:4318/v1/logs")).Endpoint);
        Assert.Null(ExportProfile.Claude(Settings(env => env["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://192.168.1.20:4318/v1/logs")).Endpoint);
        Assert.Equal("localhost", ExportProfile.Claude(Settings(env => env["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://localhost:4318/v1/logs")).Endpoint?.Host);
        Assert.Equal("[::1]", ExportProfile.Claude(Settings(env => env["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://[::1]:4318/v1/logs")).Endpoint?.Host);
    }

    [Fact]
    public void An_endpoint_whose_path_is_not_v1_logs_is_not_configured()
    {
        foreach (var endpoint in new[] { "http://127.0.0.1:4318", "http://127.0.0.1:4318/", "http://127.0.0.1:4318/v1/logs/",
                                         "http://127.0.0.1:4318/v1/traces", "http://127.0.0.1:4318/otel/v1/logs" })
        {
            Assert.Null(ExportProfile.Claude(Settings(env => env["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = endpoint)).Endpoint);
        }
    }

    [Fact]
    public void The_logs_endpoint_is_taken_from_the_generic_one_without_a_trailing_slash()
    {
        var verdict = ExportProfile.Claude(Settings(env =>
        {
            env.Remove("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT");
            env["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4400";
        }));

        Assert.Equal(new ExportEndpoint("localhost", 4400, TimeSpan.FromSeconds(5)), verdict.Endpoint);
    }

    [Fact]
    public void The_logs_endpoint_is_taken_from_the_generic_one_with_a_trailing_slash()
    {
        var verdict = ExportProfile.Claude(Settings(env =>
        {
            env.Remove("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT");
            env["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4401/";
        }));

        Assert.Equal(4401, verdict.Endpoint?.Port);
        Assert.Null(ExportProfile.Claude(Settings(env =>
        {
            env.Remove("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT");
            env["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4401/v1/logs";
        })).Endpoint);
    }

    [Fact]
    public void Settings_that_are_absent_not_JSON_or_without_an_env_object_are_not_configured()
    {
        Assert.Null(ExportProfile.Claude(null).Endpoint);
        Assert.Null(ExportProfile.Claude("{ not json").Endpoint);
        Assert.Null(ExportProfile.Claude("""{ "env": "CLAUDE_CODE_ENABLE_TELEMETRY=1" }""").Endpoint);
        Assert.Null(ExportProfile.Claude("[]").Endpoint);
    }

    [Fact]
    public void A_reason_names_keys_and_never_values()
    {
        var verdict = ExportProfile.Claude(Settings(env => env["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://secret-host.example:4318/v1/logs"));

        Assert.Null(verdict.Endpoint);
        Assert.DoesNotContain("secret", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The export interval ----

    [Fact]
    public void The_export_interval_is_read_in_milliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500),
                     ExportProfile.Claude(Settings(env => env["OTEL_LOGS_EXPORT_INTERVAL"] = "1500")).Endpoint?.Interval);
    }

    [Fact]
    public void The_export_interval_defaults_to_5000_when_absent()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(5000), ExportProfile.Claude(Settings()).Endpoint?.Interval);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), ExportProfile.Interval(null));
    }

    [Fact]
    public void The_export_interval_is_capped_at_10000()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(10_000),
                     ExportProfile.Claude(Settings(env => env["OTEL_LOGS_EXPORT_INTERVAL"] = "60000")).Endpoint?.Interval);
    }

    [Theory]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    [InlineData("3000000000")]
    [InlineData("99999999999999999999")]
    [InlineData("000000000000000000000000012345")]
    public void An_export_interval_past_what_an_int_holds_is_still_capped_at_10000(string value)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), ExportProfile.Interval(value));
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), ExportProfile.Claude(Settings(env => env["OTEL_LOGS_EXPORT_INTERVAL"] = value)).Endpoint?.Interval);
    }

    [Fact]
    public void A_long_run_of_zeros_or_a_large_negative_interval_still_defaults_to_5000()
    {
        foreach (var value in new[] { "00000000000000000000000", "-99999999999999999999", "99999999999999999999.5" })
            Assert.Equal(TimeSpan.FromMilliseconds(5000), ExportProfile.Interval(value));
        Assert.Equal(TimeSpan.FromMilliseconds(7), ExportProfile.Interval("0007"));
    }

    [Fact]
    public void An_export_interval_that_is_not_a_positive_integer_defaults_to_5000()
    {
        foreach (var value in new[] { "0", "-5", "1.5", "5s", "", " 100" })
            Assert.Equal(TimeSpan.FromMilliseconds(5000), ExportProfile.Interval(value));

        Assert.Equal(TimeSpan.FromMilliseconds(5000),
                     ExportProfile.Claude(Settings(env => env["OTEL_LOGS_EXPORT_INTERVAL"] = 100)).Endpoint?.Interval);
    }

    // ---- Codex ----

    [Fact]
    public void The_inline_spelling_configures_the_export_in_either_key_order()
    {
        Assert.Equal(new ExportEndpoint("127.0.0.1", 4318, TimeSpan.FromSeconds(5)),
                     ExportProfile.Codex("""
                                         [otel]
                                         exporter = { otlp-http = { endpoint = "http://127.0.0.1:4318/v1/logs", protocol = "json" } }
                                         """).Endpoint);
        Assert.NotNull(ExportProfile.Codex("""
                                           [otel]
                                           environment = "dev"
                                           exporter = { otlp-http = { protocol = "json", endpoint = "http://localhost:4318/v1/logs" } }
                                           """).Endpoint);
    }

    [Fact]
    public void The_inline_spelling_admits_headers_single_quotes_quoted_keys_and_a_comment()
    {
        var verdict = ExportProfile.Codex("""
                                          # Codex settings
                                          model = "gpt-5.5"

                                          [ otel ]
                                          exporter = { "otlp-http" = { endpoint = 'http://127.0.0.1:4319/v1/logs', headers = { "x-team" = "a" }, "protocol" = 'json' } }  # export
                                          """);

        Assert.Equal(4319, verdict.Endpoint?.Port);
    }

    [Fact]
    public void The_table_spelling_configures_the_export_with_headers_quotes_and_comments()
    {
        Assert.Equal(4320, ExportProfile.Codex("""
                                               [otel.exporter.otlp-http]
                                               endpoint = "http://127.0.0.1:4320/v1/logs"
                                               protocol = "json"
                                               """).Endpoint?.Port);
        Assert.Equal(4321, ExportProfile.Codex("""
                                               [mcp_servers.docs]
                                               args = [
                                                 "--port", # the port
                                                 "1",
                                               ]

                                               [otel.exporter."otlp-http"]   # export
                                               "protocol" = 'json'
                                               headers = { "x-team" = "a" }
                                               endpoint   =   'http://localhost:4321/v1/logs'   # local
                                               """).Endpoint?.Port);
    }

    [Theory]
    [InlineData("endpoint = http://127.0.0.1:47318/v1/logs", "protocol = \"json\"")]
    [InlineData("endpoint = \"http://127.0.0.1:47318/v1/logs\"", "protocol = json")]
    public void A_bare_unquoted_value_is_not_a_string_in_either_spelling(string endpoint, string protocol)
    {
        Assert.Null(ExportProfile.Codex($"[otel.exporter.otlp-http]\n{endpoint}\n{protocol}\n").Endpoint);
        Assert.Null(ExportProfile.Codex($"[otel]\nexporter = {{ otlp-http = {{ {endpoint}, {protocol} }} }}\n").Endpoint);

        // The same lines with every value quoted are configured, in both spellings.
        Assert.Equal(47318, ExportProfile.Codex("[otel.exporter.otlp-http]\nendpoint = \"http://127.0.0.1:47318/v1/logs\"\nprotocol = 'json'\n")
                                         .Endpoint?.Port);
        Assert.Equal(47318, ExportProfile.Codex("[otel]\nexporter = { otlp-http = { endpoint = 'http://127.0.0.1:47318/v1/logs', protocol = \"json\" } }\n")
                                         .Endpoint?.Port);
    }

    [Fact]
    public void A_Codex_exporter_with_another_protocol_or_endpoint_is_not_configured()
    {
        Assert.Null(ExportProfile.Codex("""
                                        [otel]
                                        exporter = { otlp-http = { endpoint = "http://127.0.0.1:4318/v1/logs", protocol = "binary" } }
                                        """).Endpoint);
        Assert.Null(ExportProfile.Codex("""
                                        [otel]
                                        exporter = { otlp-grpc = { endpoint = "http://127.0.0.1:4318/v1/logs", protocol = "json" } }
                                        """).Endpoint);
        Assert.Null(ExportProfile.Codex("""
                                        [otel.exporter.otlp-http]
                                        endpoint = "http://10.0.0.5:4318/v1/logs"
                                        protocol = "json"
                                        """).Endpoint);
        Assert.Null(ExportProfile.Codex("""
                                        [otel]
                                        exporter = "otlp-http"
                                        """).Endpoint);
        Assert.Null(ExportProfile.Codex(null).Endpoint);
    }

    [Fact]
    public void An_inline_table_spread_over_several_lines_is_not_admitted()
    {
        Assert.Null(ExportProfile.Codex("""
                                        [otel]
                                        exporter = { otlp-http = {
                                          endpoint = "http://127.0.0.1:4318/v1/logs",
                                          protocol = "json"
                                        } }
                                        """).Endpoint);
    }

    [Fact]
    public void An_inline_table_spread_over_several_lines_inside_the_table_spelling_is_not_admitted()
    {
        // The endpoint and protocol lines inside the open headers table are never read as the exporter's keys.
        Assert.Null(ExportProfile.Codex("""
                                        [otel.exporter.otlp-http]
                                        headers = {
                                          endpoint = "http://127.0.0.1:4318/v1/logs",
                                          protocol = "json"
                                        }
                                        """).Endpoint);

        // Nor does a spread headers table beside a proper endpoint and protocol configure the export.
        Assert.Null(ExportProfile.Codex("""
                                        [otel.exporter.otlp-http]
                                        endpoint = "http://127.0.0.1:4318/v1/logs"
                                        headers = { "x-team" = "a",
                                                    "x-env" = "dev" }
                                        protocol = "json"
                                        """).Endpoint);
        Assert.Null(ExportProfile.Codex("""
                                        [otel.exporter.otlp-http]
                                        endpoint = "http://127.0.0.1:4318/v1/logs"
                                        protocol = "json"
                                        headers = { auth = { "x-team" = "a",
                                          "x-key" = "}" } }
                                        """).Endpoint);

        // The same headers on one line still configure it.
        Assert.Equal(4322, ExportProfile.Codex("""
                                               [otel.exporter.otlp-http]
                                               endpoint = "http://127.0.0.1:4322/v1/logs"
                                               headers = { "x-team" = "a", "x-env" = "dev" }
                                               protocol = 'json'
                                               """).Endpoint?.Port);
    }

    [Fact]
    public void A_spread_inline_table_elsewhere_passes_over_whole_and_leaves_the_exporter_as_it_is()
    {
        // A spread table outside the exporter is skipped to its closing brace; the lines after it read as usual.
        Assert.Equal(4323, ExportProfile.Codex("""
                                               [mcp_servers.docs]
                                               env = {
                                                 A = "1"
                                               }

                                               [otel.exporter.otlp-http]
                                               endpoint = "http://127.0.0.1:4323/v1/logs"
                                               protocol = "json"
                                               """).Endpoint?.Port);

        // A spread table whose lines would read as the exporter's keys is passed over, not read.
        Assert.Null(ExportProfile.Codex("""
                                        [otel.exporter.otlp-http]
                                        endpoint = "http://127.0.0.1:4324/v1/logs"
                                        [mcp_servers.docs]
                                        env = {
                                        [otel.exporter.otlp-http]
                                        protocol = "json"
                                        }
                                        """).Endpoint);
    }

    [Fact]
    public void Dotted_keys_are_not_admitted()
    {
        Assert.Null(ExportProfile.Codex("""
                                        otel.exporter.otlp-http.endpoint = "http://127.0.0.1:4318/v1/logs"
                                        otel.exporter.otlp-http.protocol = "json"
                                        """).Endpoint);
        Assert.Null(ExportProfile.Codex("""
                                        [otel]
                                        exporter.otlp-http.endpoint = "http://127.0.0.1:4318/v1/logs"
                                        exporter.otlp-http.protocol = "json"
                                        """).Endpoint);
        Assert.Null(ExportProfile.Codex("""
                                        [otel]
                                        exporter = { otlp-http.endpoint = "http://127.0.0.1:4318/v1/logs", otlp-http.protocol = "json" }
                                        """).Endpoint);
    }

    [Fact]
    public void Multi_line_strings_are_not_admitted_and_their_lines_are_not_read_as_keys()
    {
        Assert.Null(ExportProfile.Codex("""
                                        [otel.exporter.otlp-http]
                                        endpoint = '''
                                        http://127.0.0.1:4318/v1/logs'''
                                        protocol = "json"
                                        """).Endpoint);
        Assert.Null(ExportProfile.Codex(""""
                                        instructions = """
                                        [otel.exporter.otlp-http]
                                        endpoint = "http://127.0.0.1:4318/v1/logs"
                                        protocol = "json"
                                        """
                                        """").Endpoint);
    }

    // ---- Helpers ----

    /// <summary>A complete export profile, changed by <paramref name="change"/>.</summary>
    /// <param name="change">Edits the <c>env</c> object.</param>
    private static string Settings(Action<JsonObject>? change = null)
    {
        var env = new JsonObject
        {
            ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1",
            ["OTEL_LOGS_EXPORTER"] = "otlp",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/json",
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://127.0.0.1:4318/v1/logs",
        };
        change?.Invoke(env);
        return new JsonObject { ["model"] = "opus", ["env"] = env }.ToJsonString();
    }
}
