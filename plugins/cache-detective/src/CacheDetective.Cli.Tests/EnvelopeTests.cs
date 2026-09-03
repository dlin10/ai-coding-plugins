using System.Text.Json;
using CacheDetective.Mcp;
using CacheDetective.Serialization;
using Xunit;

namespace CacheDetective.Tests;

public sealed class EnvelopeTests
{
    [Fact]
    public void Envelope_uses_default_pagination_and_reports_header()
    {
        var source = Enumerable.Range(1, 125).Select(value => value.ToString()).ToList();

        var envelope = ResponseEnvelope.Create(source, null, CacheDetectiveJsonContext.Default.ListEnvelopeString);

        Assert.Equal(125, envelope.Total);
        Assert.Equal(1, envelope.Page);
        Assert.Equal(3, envelope.Pages);
        Assert.Equal(50, envelope.Items.Count);
        Assert.Null(envelope.Notice);
    }

    [Fact]
    public void Envelope_reduces_page_instead_of_truncating_json()
    {
        var source = Enumerable.Range(1, 20)
                               .Select(value => $"{value:D2}:{new string('x', 1_000)}")
                               .ToList();
        var arguments = new PageArguments { Page = 1, PageSize = 20 };

        var envelope = ResponseEnvelope.Create(
            source, arguments, CacheDetectiveJsonContext.Default.ListEnvelopeString);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope, CacheDetectiveJsonContext.Default.ListEnvelopeString);

        Assert.True(bytes.Length <= ResponseEnvelope.MaximumSerializedBytes);
        Assert.InRange(envelope.Items.Count, 1, 19);
        Assert.Equal((int)Math.Ceiling((double)source.Count / envelope.Items.Count), envelope.Pages);
        Assert.Contains("reduced", envelope.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source.Take(envelope.Items.Count), envelope.Items);
    }
}
