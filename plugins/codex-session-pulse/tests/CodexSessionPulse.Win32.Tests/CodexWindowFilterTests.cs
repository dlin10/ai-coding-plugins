using Xunit;

namespace CodexSessionPulse.Win32.Tests;

/// <summary>The §3.3 rule with the defaults measured in docs/compatibility.md §6.</summary>
public sealed class CodexWindowFilterTests
{
    private const string CodexPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1103.3517.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
    private const string ConsumerChatGptPath = @"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1.2025.1.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";

    /// <summary>The visible unowned Codex Desktop window matches the defaults.</summary>
    [Fact]
    public void Default_MatchesCodexDesktopWindow()
    {
        Assert.True(CodexWindowFilter.IsCodexWindow(Window(CodexPath), WindowDetectorOptions.Default));
    }

    /// <summary>The ChatGPT consumer app has the same process name but lives in another package.</summary>
    [Fact]
    public void Default_RejectsConsumerChatGptApp()
    {
        Assert.False(CodexWindowFilter.IsCodexWindow(Window(ConsumerChatGptPath), WindowDetectorOptions.Default));
    }

    /// <summary>Without path filters the process name alone decides.</summary>
    [Fact]
    public void EmptyPathFilters_AcceptAnyPathOfAMatchingProcess()
    {
        WindowDetectorOptions options = WindowDetectorOptions.Default with { ExecutablePathFilters = [] };

        Assert.True(CodexWindowFilter.IsCodexWindow(Window(ConsumerChatGptPath), options));
        Assert.False(CodexWindowFilter.IsCodexWindow(Window(@"C:\Windows\notepad.exe"), options));
    }

    /// <summary>Process names compare without case and without the extension.</summary>
    [Fact]
    public void ProcessName_IsCaseInsensitive()
    {
        WindowDetectorOptions options = new(["chatgpt"], null, []);

        Assert.True(CodexWindowFilter.IsCodexWindow(Window(@"D:\x\CHATGPT.EXE"), options));
    }

    /// <summary>Invisible, cloaked and owned windows, and windows whose process could not be opened, never count.</summary>
    [Fact]
    public void HiddenCloakedOwnedOrUnreadable_AreNotCodexWindows()
    {
        WindowDetectorOptions options = WindowDetectorOptions.Default;

        Assert.False(CodexWindowFilter.IsCodexWindow(Window(CodexPath) with { IsVisible = false }, options));
        Assert.False(CodexWindowFilter.IsCodexWindow(Window(CodexPath) with { IsCloaked = true }, options));
        Assert.False(CodexWindowFilter.IsCodexWindow(Window(CodexPath) with { HasOwner = true }, options));
        Assert.False(CodexWindowFilter.IsCodexWindow(Window(CodexPath) with { ExecutablePath = null }, options));
    }

    /// <summary>The title filter is a case-insensitive substring; empty means any title.</summary>
    [Theory]
    [InlineData(null, "ChatGPT", true)]
    [InlineData("", "ChatGPT", true)]
    [InlineData("chat", "ChatGPT", true)]
    [InlineData("Codex", "ChatGPT", false)]
    public void TitleFilter_IsASubstring(string? filter, string title, bool expected)
    {
        WindowDetectorOptions options = WindowDetectorOptions.Default with { TitleFilter = filter };

        Assert.Equal(expected, CodexWindowFilter.IsCodexWindow(Window(CodexPath) with { Title = title }, options));
    }

    /// <summary>The rule description names the rule only.</summary>
    [Fact]
    public void Describe_NamesTheRule()
    {
        Assert.Equal(@"processes [Codex, ChatGPT], title any, path \WindowsApps\OpenAI.Codex_, visible unowned top-level windows",
                     WindowDetectorOptions.Default.Describe());
    }

    private static WindowCandidate Window(string path) => new(42, 7, path, "ChatGPT", IsVisible: true, IsCloaked: false, HasOwner: false);
}
