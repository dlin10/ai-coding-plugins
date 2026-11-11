using CodexSessionPulse.Core;
using Xunit;

namespace CodexSessionPulse.Win32.Tests;

/// <summary>
/// Which UI Automation elements the input locator takes for the editor and its card. The rectangles are the raw-view
/// chains measured in Codex Desktop on 2026-11-09 (docs/compatibility.md §6.2).
/// </summary>
public sealed class CodexInputLocatorTests
{
    private static readonly WindowBounds Window = new(323, 108, 1796, 1068);
    private static readonly WindowBounds Editor = new(987, 1047, 890, 50);
    private static readonly WindowBounds Body = new(972, 1029, 920, 123);

    /// <summary>The composer's ProseMirror editor is taken; the settings search box, also an Edit, is not.</summary>
    [Fact]
    public void PickEditor_TakesTheProseMirrorEditor()
    {
        EditorCandidate[] candidates =
        [
            new("min-w-0 flex-1 bg-transparent text-default outline-none", new WindowBounds(441, 238, 280, 23)),
            new("ProseMirror ProseMirror-focused", Editor),
        ];

        Assert.Equal(1, CodexInputLocator.PickEditor(candidates, Window));
        Assert.Equal(-1, CodexInputLocator.PickEditor(candidates[..1], Window));
    }

    /// <summary>Of two editors the lowest is the composer; one without a rectangle or outside the window never counts.</summary>
    [Fact]
    public void PickEditor_TakesTheLowestEditorInsideTheWindow()
    {
        EditorCandidate[] candidates =
        [
            new("ProseMirror", new WindowBounds(987, 400, 890, 50)),
            new("ProseMirror", Editor),
            new("ProseMirror", null),
            new("ProseMirror", new WindowBounds(987, 2000, 890, 50)),
            new(null, new WindowBounds(987, 1100, 890, 50)),
        ];

        Assert.Equal(1, CodexInputLocator.PickEditor(candidates, Window));
    }

    /// <summary>In a thread at 125% the card is the composer's outer box; the footer above it spans the chat column.</summary>
    [Fact]
    public void PickCard_Thread_TakesTheComposerBox()
    {
        WindowBounds?[] ancestors =
        [
            Body, Body, Body, Body, new WindowBounds(952, 1029, 960, 123), new WindowBounds(749, 1029, 1366, 143),
            new WindowBounds(749, 228, 1366, 944),
        ];

        Assert.Equal(4, CodexInputLocator.PickCard(Editor, ancestors, 1.25));
    }

    /// <summary>In a new chat the card also holds the row of project chips above the editor.</summary>
    [Fact]
    public void PickCard_NewChat_TakesTheBoxWithTheChips()
    {
        WindowBounds?[] ancestors =
        [
            Body, Body, Body, new WindowBounds(972, 969, 920, 183), new WindowBounds(952, 959, 960, 193),
            new WindowBounds(762, 952, 1340, 220),
        ];

        Assert.Equal(4, CodexInputLocator.PickCard(Editor, ancestors, 1.25));
    }

    /// <summary>
    /// After the window moved to a 100% monitor, the display: contents wrapper kept its 125% size and is no longer centred
    /// on the editor; it is skipped, and the box with the chips above it is taken.
    /// </summary>
    [Fact]
    public void PickCard_StaleWrapperAfterADpiChange_IsSkipped()
    {
        WindowBounds editor = new(-1147, 978, 712, 40);
        WindowBounds body = new(-1159, 964, 736, 98);
        WindowBounds stale = new(-1155, 918, 920, 183);
        WindowBounds?[] ancestors = [body, body, body, stale, new WindowBounds(-1175, 908, 768, 154), new WindowBounds(-1327, 902, 1072, 176)];

        Assert.False(CodexInputLocator.FitsAround(stale, editor, 1.0));
        Assert.Equal(4, CodexInputLocator.PickCard(editor, ancestors, 1.0));
    }

    /// <summary>An ancestor without a rectangle is skipped; walking up stops at the first one that no longer hugs the editor.</summary>
    [Fact]
    public void PickCard_SkipsAncestorsWithoutARectangle()
    {
        WindowBounds?[] ancestors = [null, Body, new WindowBounds(952, 959, 960, 193), new WindowBounds(749, 228, 1366, 944),
                                     new WindowBounds(952, 900, 960, 252)];

        Assert.Equal(2, CodexInputLocator.PickCard(Editor, ancestors, 1.25));
    }

    /// <summary>A column as narrow as the card but holding the transcript too is not the card; neither is the chat column.</summary>
    [Fact]
    public void PickCard_RejectsTallOrWideAncestors()
    {
        Assert.Equal(0, CodexInputLocator.PickCard(Editor, [Body, new WindowBounds(972, 228, 920, 944)], 1.25));
        Assert.Equal(-1, CodexInputLocator.PickCard(Editor, [new WindowBounds(749, 228, 1366, 944)], 1.25));
        Assert.Equal(-1, CodexInputLocator.PickCard(Editor, [], 1.25));
    }

    /// <summary>A card fits around its editor when it contains it and their centres agree within 2 DIP.</summary>
    [Fact]
    public void FitsAround_NeedsTheEditorInsideAndCentred()
    {
        Assert.True(CodexInputLocator.FitsAround(Body, Editor, 1.25));
        Assert.True(CodexInputLocator.FitsAround(Body with { Left = Body.Left + 2 }, Editor, 1.25));
        Assert.False(CodexInputLocator.FitsAround(Body with { Left = Body.Left + 4 }, Editor, 1.25));
        Assert.False(CodexInputLocator.FitsAround(Body with { Top = Editor.Top + 1 }, Editor, 1.25));
        Assert.True(CodexInputLocator.FitsAround(Editor, Editor, 1.25));
    }
}
