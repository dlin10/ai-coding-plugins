using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Codex parses a `-c key=value` override's value as TOML, so the role prompt, effort and sandbox
/// mode passed that way have to come out as a single-line TOML basic string.
/// </summary>
public sealed class TomlValueTests
{
    [Fact]
    public void A_plain_word_is_just_quoted()
    {
        Assert.Equal("\"hello\"", TomlValue.String("hello"));
    }

    [Fact]
    public void A_double_quote_is_escaped()
    {
        Assert.Equal("\"say \\\"hi\\\"\"", TomlValue.String("say \"hi\""));
    }

    [Fact]
    public void A_backslash_is_escaped()
    {
        Assert.Equal("\"C:\\\\path\"", TomlValue.String("C:\\path"));
    }

    [Fact]
    public void A_newline_is_escaped()
    {
        Assert.Equal("\"line one\\nline two\"", TomlValue.String("line one\nline two"));
    }

    [Fact]
    public void A_control_character_is_escaped_as_a_unicode_sequence()
    {
        Assert.Equal("\"a\\u0001b\"", TomlValue.String("a\u0001b"));
    }
}
