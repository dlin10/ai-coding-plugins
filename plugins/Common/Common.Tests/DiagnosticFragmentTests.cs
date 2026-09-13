using System.Text.Json;
using Common.Mcp;
using Common.Roslyn;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Common.Tests;

/// <summary>
/// A diagnostic message is cut into fragments that fit once serialized. Cutting by UTF-16 characters was
/// not the same thing: a Cyrillic message costs six bytes a character after JSON escaping, so 1500
/// characters reached about 9000 bytes and the envelope answered with an empty page and a notice.
/// </summary>
public sealed class DiagnosticFragmentTests
{
    [Fact]
    public void A_long_cyrillic_message_pages_without_an_empty_page()
    {
        var message = string.Concat(Enumerable.Repeat("Не удалось загрузить проект: ", 200));
        Assert.True(message.Length > 4000, $"the fixture message was only {message.Length} characters");

        var described = WorkspaceDiagnostics.Describe([new FakeDiagnostic(message)]);

        Assert.All(described, fragment => Assert.NotEmpty(fragment.Message));
        Assert.All(described, fragment => Assert.True(Size(fragment) <= ResponseEnvelope.MaximumSerializedBytes,
                                                       $"a fragment serialized to {Size(fragment)} bytes"));
        Assert.Equal(message, string.Concat(described.OrderBy(fragment => fragment.Part).Select(fragment => fragment.Message)));
    }

    /// <summary>Every page of the paged result carries at least one fragment: an oversized item is what
    /// produced an empty page, and there are none left.</summary>
    [Fact]
    public void Every_page_of_a_long_message_carries_something()
    {
        var described = WorkspaceDiagnostics.Describe([new FakeDiagnostic(string.Concat(Enumerable.Repeat("Ошибка сборки. ", 400)))]);

        for (var page = 1; page <= described.Count; page++)
        {
            var envelope = DiagnosticFragments.Page(described, new PageArguments { Page = page, PageSize = 1 });
            Assert.NotEmpty(envelope.Items);
        }
    }

    /// <summary>A cut never lands between the halves of a surrogate pair, or the rejoined text would differ from the original by two broken characters.</summary>
    [Fact]
    public void A_surrogate_pair_is_never_split()
    {
        var message = string.Concat(Enumerable.Repeat("🧭семь", 900));

        var described = WorkspaceDiagnostics.Describe([new FakeDiagnostic(message)]);

        Assert.All(described, fragment => Assert.False(char.IsHighSurrogate(fragment.Message[^1]),
                                                        "a fragment ended on an unpaired high surrogate"));
        Assert.Equal(message, string.Concat(described.OrderBy(fragment => fragment.Part).Select(fragment => fragment.Message)));
    }

    private static int Size(WorkspaceDiagnosticResult fragment) =>
        JsonSerializer.SerializeToUtf8Bytes(fragment, CommonJsonContext.Default.WorkspaceDiagnosticResult).Length;

    private sealed class FakeDiagnostic(string message) : WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, message);
}
