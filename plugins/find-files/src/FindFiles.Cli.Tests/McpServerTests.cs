using System.Text.Json;
using FindFiles.Everything;
using FindFiles.Mcp;
using Xunit;

namespace FindFiles.Tests;

public sealed class McpServerTests
{
    private static McpServer Server(FakeEsRunner? runner = null) =>
        new(new SearchService(runner));

    private static JsonElement Handle(McpServer server, string request)
    {
        var response = server.HandleLine(request);
        Assert.NotNull(response);
        return JsonDocument.Parse(response).RootElement.Clone();
    }

    [Fact]
    public void InitializeAdvertisesToolsAndEchoesTheClientProtocolVersion()
    {
        var response = Handle(Server(),
                              """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""");

        var result = response.GetProperty("result");
        Assert.Equal("2099-01-01", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal(BuildInfo.ServerName, result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(1, response.GetProperty("id").GetInt32());
    }

    [Fact]
    public void InitializeFallsBackToAKnownProtocolVersionWhenTheClientOmitsOne()
    {
        var response = Handle(Server(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.False(string.IsNullOrWhiteSpace(response.GetProperty("result")
                                                      .GetProperty("protocolVersion")
                                                      .GetString()));
    }

    [Fact]
    public void ToolListExposesExactlyOneToolWithARequiredQuery()
    {
        var response = Handle(Server(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = response.GetProperty("result").GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());

        var tool = tools[0];
        Assert.Equal(ToolDefinition.ToolName, tool.GetProperty("name").GetString());

        var schema = tool.GetProperty("inputSchema");
        Assert.Equal("query", schema.GetProperty("required")[0].GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("path", out _));
    }

    [Fact]
    public void TheToolDescriptionStatesWhereNotToUseItAsWellAsWhereToUseIt()
    {
        var description = ToolDefinition.Description;

        // A description that only advertises capability gets the tool used inside repositories,
        // where an index that ignores .gitignore makes results worse rather than better.
        Assert.Contains("Do NOT", description);
        Assert.Contains(".gitignore", description);
        Assert.Contains("does NOT search file contents", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolCallReturnsTheRenderedTextAsContent()
    {
        var runner = new FakeEsRunner()
            .Enqueue(EsExitCode.Success, [@"C:\a\SKILL.md"])
            .Enqueue(EsExitCode.Success, ["1"]);

        var response = Handle(Server(runner),
                              """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"find_file_anywhere","arguments":{"query":"SKILL.md"}}}""");

        var content = response.GetProperty("result").GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Contains(@"C:\a\SKILL.md", content[0].GetProperty("text").GetString());
        Assert.False(response.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public void AStoppedEngineIsAnAnswerNotAProtocolError()
    {
        var runner = new FakeEsRunner().Enqueue(EsExitCode.EverythingNotRunning);

        var response = Handle(Server(runner),
                              """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"find_file_anywhere","arguments":{"query":"x"}}}""");

        var result = response.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Contains("Everything is not running", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void AnUnknownToolNameIsAnInvalidParamsError()
    {
        var response = Handle(Server(),
                              """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"grep","arguments":{}}}""");

        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void AnUnsupportedMethodGetsMethodNotFoundRatherThanSilence()
    {
        // Hosts probe for resources and prompts; a dead pipe there looks like a broken server.
        var response = Handle(Server(), """{"jsonrpc":"2.0","id":6,"method":"resources/list"}""");

        Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void NotificationsAreNotAnswered()
    {
        Assert.Null(Server().HandleLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
    }

    [Fact]
    public void MalformedJsonBecomesAParseErrorInsteadOfACrash()
    {
        var response = Handle(Server(), "{not json");

        Assert.Equal(-32700, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void AStringRequestIdIsEchoedWithItsOriginalType()
    {
        var response = Handle(Server(), """{"jsonrpc":"2.0","id":"abc","method":"tools/list"}""");

        Assert.Equal("abc", response.GetProperty("id").GetString());
    }

    [Fact]
    public void EveryResponseIsASingleLineSoTheTransportStaysFramed()
    {
        var response = Server().HandleLine("""{"jsonrpc":"2.0","id":7,"method":"tools/list"}""");

        Assert.NotNull(response);
        Assert.DoesNotContain('\n', response);
        Assert.DoesNotContain('\r', response);
    }
}
