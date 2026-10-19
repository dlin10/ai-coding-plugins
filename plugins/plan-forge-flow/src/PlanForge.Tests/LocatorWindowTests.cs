using PlanForge.Acts;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Checks local locator resolution and the exact normalized window it fingerprints.</summary>
public sealed class LocatorWindowTests : IDisposable
{
    /// <summary>The isolated repository root for each test.</summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-locators", Guid.NewGuid().ToString("n"));

    /// <summary>Creates a temporary repository root.</summary>
    public LocatorWindowTests() => Directory.CreateDirectory(_root);

    /// <summary>Removes the files created by this test.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A middle line includes five lines on either side.</summary>
    [Fact]
    public void A_middle_line_hashes_an_eleven_line_window()
    {
        Write("source.cs", Lines(1, 20));
        AssertWindow("source.cs:10", Lines(5, 11));
    }

    /// <summary>The start of a file clamps the window.</summary>
    [Fact]
    public void A_line_near_the_start_clamps_the_window()
    {
        Write("source.cs", Lines(1, 20));
        AssertWindow("source.cs:2", Lines(1, 7));
    }

    /// <summary>The end of a file clamps the window.</summary>
    [Fact]
    public void A_line_near_the_end_clamps_the_window()
    {
        Write("source.cs", Lines(1, 20));
        AssertWindow("source.cs:19", Lines(14, 7));
    }

    /// <summary>A line beyond the file has no window.</summary>
    [Fact]
    public void A_line_past_the_end_is_out_of_range()
    {
        Write("source.cs", "one\ntwo");
        AssertReason("source.cs:3", "line_out_of_range");
    }

    /// <summary>A trailing newline is not an extra source line.</summary>
    [Fact]
    public void A_final_line_break_adds_no_line()
    {
        Write("source.cs", "one\ntwo\n");
        AssertReason("source.cs:3", "line_out_of_range");
        AssertWindow("source.cs:2", "one\ntwo");
    }

    /// <summary>Both common newline styles produce the same fingerprint.</summary>
    [Fact]
    public void Lf_and_crlf_have_the_same_hash()
    {
        Write("lf.cs", Lines(1, 20) + "\n");
        Write("crlf.cs", (Lines(1, 20) + "\n").Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(Check("lf.cs:10"), Check("crlf.cs:10"));
        AssertWindow("crlf.cs:10", Lines(5, 11));
    }

    /// <summary>A UTF-8 BOM is removed before hashing.</summary>
    [Fact]
    public void A_leading_byte_order_mark_is_ignored()
    {
        Write("bom.cs", "\uFEFFПривет 😀\nsecond");
        AssertWindow("bom.cs:1", "Привет 😀\nsecond");
    }

    /// <summary>The hash is precisely the existing outline hash of the window text.</summary>
    [Fact]
    public void The_window_hash_equals_the_outline_hash()
    {
        Write("source.cs", "abc");
        var result = Check("source.cs:1");
        Assert.Equal(PlanOutline.Hash("abc"), result.Window);
        Assert.Matches("^[0-9a-f]{16}$", result.Window!);
        AssertWindow("source.cs:1", "abc");
    }

    /// <summary>A symbol centres on the first whole-word occurrence.</summary>
    [Fact]
    public void A_symbol_uses_its_first_whole_word_line()
    {
        Write("source.cs", "MemberSuffix\nMember()\n" + Lines(3, 10) + "\nMember()");
        AssertWindow("source.cs#Member", "MemberSuffix\nMember()\n" + Lines(3, 5));
    }

    /// <summary>Identifier neighbours prevent a symbol match.</summary>
    /// <param name="text">Source text whose identifiers must not match the requested symbol.</param>
    [Theory]
    [InlineData("MemberSuffix")]
    [InlineData("PrefixMember")]
    [InlineData("_Member Member_ 1Member Member2")]
    [InlineData("éMember Memberé")]
    public void A_symbol_inside_longer_identifiers_is_not_found(string text)
    {
        Write("source.cs", text);
        AssertReason("source.cs#Member", "symbol_not_found");
    }

    /// <summary>Only the last segment of a qualified symbol is searched.</summary>
    [Fact]
    public void A_qualified_symbol_searches_its_member()
    {
        Write("source.cs", "void Member() {}");
        AssertWindow("source.cs#Namespace.Type.Member", "void Member() {}");
    }

    /// <summary>A parameter list is removed before taking the final symbol segment.</summary>
    [Fact]
    public void A_symbol_parameter_list_is_removed()
    {
        Write("source.cs", "void Member(int value) {}");
        AssertWindow("source.cs#Type.Member(System.Int32)", "void Member(int value) {}");
    }

    /// <summary>Generic arguments are removed before taking the final symbol segment.</summary>
    [Fact]
    public void A_symbol_generic_arguments_are_removed()
    {
        Write("source.cs", "void Member<T>() {}");
        AssertWindow("source.cs#Type.Member<System.String>(int)", "void Member<T>() {}");
    }

    /// <summary>An absent path is reported locally.</summary>
    [Fact]
    public void A_missing_file_is_reported() => AssertReason("absent.cs:1", "missing_file");

    /// <summary>Absolute paths are refused even when they point inside the workspace.</summary>
    [Fact]
    public void An_absolute_path_is_outside_the_workspace()
    {
        Write("source.cs", "one");
        AssertReason(Path.Combine(_root, "source.cs") + ":1", "outside_workspace");
    }

    /// <summary>Parent traversal cannot escape the workspace.</summary>
    [Fact]
    public void A_parent_path_escaping_the_root_is_outside() =>
        AssertReason("../elsewhere.cs:1", "outside_workspace");

    /// <summary>A neighbouring path sharing the root's prefix is still outside.</summary>
    [Fact]
    public void A_sibling_with_the_same_root_prefix_is_outside() =>
        AssertReason("../" + Path.GetFileName(_root) + "-sibling/source.cs:1", "outside_workspace");

    /// <summary>A file over four MiB is not read.</summary>
    [Fact]
    public void A_file_over_four_mebibytes_is_unreadable()
    {
        Write("large.cs", new string('a', 4 * 1024 * 1024 + 1));
        AssertReason("large.cs:1", "unreadable");
    }

    /// <summary>A file at the byte limit remains readable.</summary>
    [Fact]
    public void A_file_at_four_mebibytes_is_readable()
    {
        var text = new string('a', 4 * 1024 * 1024);
        Write("large.cs", text);
        AssertWindow("large.cs:1", text);
    }

    /// <summary>The grammar accepts malformed paths which the resolver must reject without throwing.</summary>
    /// <param name="source">The accepted locator containing an invalid path.</param>
    [Theory]
    [InlineData("bad\0name.cs:1")]
    [InlineData("bad\u0001name.cs:1")]
    public void An_invalid_path_is_reported_without_throwing(string source) => AssertReason(source, "invalid_path");

    /// <summary>The grammar accepts these symbols, but cutting them leaves no searchable segment.</summary>
    /// <param name="symbol">The accepted symbol whose searchable segment is empty.</param>
    [Theory]
    [InlineData("(int)")]
    [InlineData("<T>")]
    [InlineData("Namespace.")]
    [InlineData("Namespace.  (int)")]
    public void An_invalid_symbol_is_reported_without_throwing(string symbol)
    {
        Write("source.cs", "Member");
        AssertReason("source.cs#" + symbol, "invalid_symbol");
    }

    /// <summary>Path validation precedes symbol validation.</summary>
    [Fact]
    public void An_invalid_path_precedes_an_invalid_symbol() => AssertReason("bad\0name.cs#(int)", "invalid_path");

    /// <summary>Containment is checked before symbol validity or file existence.</summary>
    [Fact]
    public void An_outside_path_precedes_an_invalid_symbol() => AssertReason("../absent.cs#(int)", "outside_workspace");

    /// <summary>A symbol is validated before checking whether the file exists.</summary>
    [Fact]
    public void An_invalid_symbol_precedes_a_missing_file() => AssertReason("absent.cs#(int)", "invalid_symbol");

    /// <summary>An empty file has no first source line.</summary>
    [Fact]
    public void An_empty_file_has_no_line()
    {
        Write("empty.cs", "");
        AssertReason("empty.cs:1", "line_out_of_range");
    }

    /// <summary>Symbol matching is ordinal and case sensitive.</summary>
    [Fact]
    public void A_differently_cased_symbol_is_not_found()
    {
        Write("source.cs", "member");
        AssertReason("source.cs#Member", "symbol_not_found");
    }

    /// <summary>Both relative path separator styles identify the same file.</summary>
    [Fact]
    public void Relative_paths_accept_both_separator_styles()
    {
        Write("src/source.cs", "one");
        AssertWindow("src/source.cs:1", "one");
        AssertWindow("src\\source.cs:1", "one");
    }

    /// <summary>An existing file that cannot be opened is unreadable.</summary>
    [Fact]
    public void A_locked_file_is_unreadable()
    {
        var path = Write("source.cs", "one");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        AssertReason("source.cs:1", "unreadable");
    }

    /// <summary>Writes a fixture through the repository's file writer.</summary>
    /// <param name="relativePath">The path under this test's root.</param>
    /// <param name="text">The exact UTF-8 file contents.</param>
    private string Write(string relativePath, string text)
    {
        var path = Path.Combine(_root, relativePath);
        AtomicFile.Write(path, text);
        return path;
    }

    /// <summary>Confirms the grammar accepts a locator before resolving it.</summary>
    /// <param name="source">The repository locator to check.</param>
    private LocatorCheck Check(string source)
    {
        Assert.True(ScoutItem.IsRepositoryLocator(source), "The locator must first pass the Scout grammar.");
        return LocatorWindow.Check(_root, source);
    }

    /// <summary>Checks every field of a resolved locator.</summary>
    /// <param name="source">The repository locator.</param>
    /// <param name="expectedText">The expected normalized window before hashing.</param>
    private void AssertWindow(string source, string expectedText) =>
        Assert.Equal(new LocatorCheck(true, PlanOutline.Hash(expectedText), null), Check(source));

    /// <summary>Checks every field of an unresolved locator.</summary>
    /// <param name="source">The repository locator.</param>
    /// <param name="reason">The expected first failed check.</param>
    private void AssertReason(string source, string reason) => Assert.Equal(new LocatorCheck(false, null, reason), Check(source));

    /// <summary>Builds distinguishable lines for exact window assertions.</summary>
    /// <param name="start">The number of the first line.</param>
    /// <param name="count">The number of lines to produce.</param>
    private static string Lines(int start, int count) => string.Join("\n", Enumerable.Range(start, count).Select(n => $"line {n}"));
}
