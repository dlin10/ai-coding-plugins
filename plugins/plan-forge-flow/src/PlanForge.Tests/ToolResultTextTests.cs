using ModelContextProtocol.Protocol;
using PlanForge.Mcp;
using Xunit;

namespace PlanForge.Tests;

public sealed class ToolResultTextTests
{
    [Fact]
    public void An_indented_escaped_result_goes_out_compact_with_its_text_as_written()
    {
        const string sent = "{\r\n  \"summary\": \"\\u0417\\u0430\\u0434\\u0430\\u0447\\u0430 \\u0027ok\\u0027 \\u003Cdone\\u003E \\u0026 \\u2014\",\r\n"
                            + "  \"seconds\": 42.70,\r\n  \"files\": [\r\n    \"a.cs\"\r\n  ],\r\n  \"gate\": null\r\n}";

        Assert.Equal("{\"summary\":\"Задача 'ok' <done> & —\",\"seconds\":42.70,\"files\":[\"a.cs\"],\"gate\":null}",
                     ToolResultText.Compacted(sent));
    }

    [Fact]
    public void What_JSON_itself_must_escape_stays_escaped()
    {
        Assert.Equal("{\"text\":\"line\\nbreak \\\"quoted\\\" back\\\\slash\"}",
                     ToolResultText.Compacted("{ \"text\": \"line\\nbreak \\\"quoted\\\" back\\\\slash\" }"));
    }

    [Theory]
    [InlineData(@"C:\work\.forge\run\forge.log")]
    [InlineData("forge.plan.patch requires baseHash")]
    [InlineData("{ not json")]
    [InlineData("")]
    public void A_text_that_is_not_a_JSON_object_or_array_passes_untouched(string text)
    {
        Assert.Equal(text, ToolResultText.Compacted(text));
    }

    [Fact]
    public async Task The_filter_rewrites_every_text_block_the_tool_answered_with()
    {
        var result = await ToolResultText.Compact((_, _) => ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = "{\n  \"state\": \"\\u0433\\u043E\\u0442\\u043E\\u0432\\u043E\"\n}" },
                       new TextContentBlock { Text = "plain words" }]
        }))(null!, CancellationToken.None);

        Assert.Equal(["{\"state\":\"готово\"}", "plain words"],
                     result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }
}
