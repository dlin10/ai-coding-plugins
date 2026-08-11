using System.Text.Json;
using FindFiles.Everything;
using FindFiles.Mcp;
using Xunit;

namespace FindFiles.Tests;

public sealed class ToolArgumentsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void AQueryIsRequired()
    {
        Assert.False(ToolArguments.TryRead(Parse("""{"path":"C:\\Dev"}"""), out _, out var error));
        Assert.Contains("query", error);
    }

    [Fact]
    public void AnEmptyQueryIsRejected()
    {
        Assert.False(ToolArguments.TryRead(Parse("""{"query":"   "}"""), out _, out _));
    }

    [Fact]
    public void DefaultsAreAppliedWhenOnlyAQueryIsGiven()
    {
        Assert.True(ToolArguments.TryRead(Parse("""{"query":"SKILL.md"}"""), out var request, out _));

        Assert.Equal(SearchRequest.DefaultLimit, request.Limit);
        Assert.Equal(EntryKind.Both, request.Kind);
        Assert.Null(request.Path);
        Assert.False(request.Recent);
        Assert.False(request.Regex);
    }

    [Fact]
    public void AnOversizedLimitIsCappedRatherThanRejected()
    {
        Assert.True(ToolArguments.TryRead(Parse("""{"query":"x","limit":100000}"""), out var request, out _));

        Assert.Equal(ToolArguments.MaxLimit, request.Limit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("\"abc\"")]
    public void AnUnusableLimitIsAnArgumentError(string limitJson)
    {
        Assert.False(ToolArguments.TryRead(Parse($$"""{"query":"x","limit":{{limitJson}}}"""), out _, out var error));
        Assert.Contains("limit", error);
    }

    [Fact]
    public void AStringifiedLimitIsAccepted()
    {
        Assert.True(ToolArguments.TryRead(Parse("""{"query":"x","limit":"5"}"""), out var request, out _));

        Assert.Equal(5, request.Limit);
    }

    // The expected kind is passed as text because xUnit needs public test methods and EntryKind is internal.
    [Theory]
    [InlineData("files", "Files")]
    [InlineData("folders", "Folders")]
    [InlineData("BOTH", "Both")]
    public void KindIsCaseInsensitive(string kind, string expected)
    {
        Assert.True(ToolArguments.TryRead(Parse($$"""{"query":"x","kind":"{{kind}}"}"""), out var request, out _));

        Assert.Equal(Enum.Parse<EntryKind>(expected), request.Kind);
    }

    [Fact]
    public void AnUnknownKindIsAnArgumentError()
    {
        Assert.False(ToolArguments.TryRead(Parse("""{"query":"x","kind":"symlinks"}"""), out _, out var error));
        Assert.Contains("kind", error);
    }

    [Fact]
    public void ABlankPathIsTreatedAsNoScope()
    {
        Assert.True(ToolArguments.TryRead(Parse("""{"query":"x","path":"  "}"""), out var request, out _));

        Assert.Null(request.Path);
    }

    [Fact]
    public void ANonObjectArgumentsPayloadIsRejected()
    {
        Assert.False(ToolArguments.TryRead(Parse("\"SKILL.md\""), out _, out _));
    }
}
