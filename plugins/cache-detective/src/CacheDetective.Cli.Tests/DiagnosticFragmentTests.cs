using System.Text;
using System.Text.Json;
using CacheDetective.Mcp;
using CacheDetective.Serialization;
using Microsoft.CodeAnalysis;
using Xunit;

namespace CacheDetective.Tests;

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

        var described = WorkspaceSession.Describe([new FakeDiagnostic(message)]);

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
        var described = WorkspaceSession.Describe([new FakeDiagnostic(string.Concat(Enumerable.Repeat("Ошибка сборки. ", 400)))]);

        for (var page = 1; page <= described.Count; page++)
        {
            var envelope = WorkspaceSession.PageDiagnostics(described, new PageArguments { Page = page, PageSize = 1 });
            Assert.NotEmpty(envelope.Items);
        }
    }

    /// <summary>A cut never lands between the halves of a surrogate pair, or the rejoined text would differ from the original by two broken characters.</summary>
    [Fact]
    public void A_surrogate_pair_is_never_split()
    {
        var message = string.Concat(Enumerable.Repeat("🧭семь", 900));

        var described = WorkspaceSession.Describe([new FakeDiagnostic(message)]);

        Assert.All(described, fragment => Assert.False(char.IsHighSurrogate(fragment.Message[^1]),
                                                        "a fragment ended on an unpaired high surrogate"));
        Assert.Equal(message, string.Concat(described.OrderBy(fragment => fragment.Part).Select(fragment => fragment.Message)));
    }

    /// <summary>
    /// A whole index result stays inside the response limit, not only the diagnostics nested in it. A
    /// solution with hundreds of unloadable projects carries a missingProjects list that can exceed the
    /// limit on its own.
    /// </summary>
    [Fact]
    public void An_index_result_with_hundreds_of_missing_projects_stays_under_the_limit()
    {
        var missing = ManyMissing();

        var page = Recall(missing, [], null);

        Assert.True(Size(page) <= ResponseEnvelope.MaximumSerializedBytes, $"the result was {Size(page)} bytes");
        Assert.NotEmpty(page.MissingProjects!);
        Assert.True(page.MissingProjects!.Count < missing.Length, "nothing was trimmed");
    }

    /// <summary>
    /// The names that do not fit are counted and carried, not dropped. SKILL.md tells the agent that
    /// missingProjects names the projects the load did not open, so a silently trimmed prefix would be
    /// read as the whole list.
    /// </summary>
    [Fact]
    public void An_index_result_with_hundreds_of_missing_projects_says_what_it_hid()
    {
        var missing = ManyMissing();

        var page = Recall(missing, [], null);

        Assert.True(Size(page) <= ResponseEnvelope.MaximumSerializedBytes, $"the result was {Size(page)} bytes");
        Assert.Equal(missing.Length, page.MissingProjects!.Count + page.MissingProjectsHidden);
        Assert.True(page.MissingProjectsHidden > 0, "nothing was reported as hidden");
    }

    /// <summary>
    /// A hundred real diagnostics beside eight hundred missing projects, read page by page as an agent
    /// reads them. Everything must come back: every diagnostic, every project name, one page count, ids
    /// that do not collide, and no page over the limit.
    /// <para>Each of those failed before. The carried names were appended to an already-sliced page and
    /// that mixture was re-partitioned per page, so pages disagreed about how many there were and half the
    /// diagnostics appeared on none of them; and the carried fragments were numbered from d:1, so they
    /// collided with the first real diagnostic and rejoining by id spliced two messages together.</para>
    /// </summary>
    [Fact]
    public void Every_diagnostic_and_missing_project_is_reachable_through_the_pages()
    {
        var missing = ManyMissing();
        var real = WorkspaceSession.Describe(Enumerable.Range(0, 100)
                                                       .Select(index => (Microsoft.CodeAnalysis.WorkspaceDiagnostic)
                                                                   new FakeDiagnostic($"diagnostic number {index:D3}: the project could not be read"))
                                                       .ToArray());

        var first = Recall(missing, real, new PageArguments { Page = 1, PageSize = 50 });
        var seen = new List<WorkspaceDiagnosticResult>();
        for (var page = 1; page <= first.Diagnostics.Pages; page++)
        {
            var current = Recall(missing, real, new PageArguments { Page = page, PageSize = 50 });
            Assert.Equal(first.Diagnostics.Pages, current.Diagnostics.Pages);
            Assert.True(Size(current) <= ResponseEnvelope.MaximumSerializedBytes, $"page {page} was {Size(current)} bytes");
            seen.AddRange(current.Diagnostics.Items);
        }

        // Every real diagnostic came back on some page. Told apart by id, not by the text: only the first
        // fragment of the carried message carries its prefix, so filtering on that would count the rest of
        // its fragments as diagnostics.
        var realIds = real.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(real.Count, seen.Count(item => realIds.Contains(item.Id)));
        Assert.Equal(realIds, seen.Where(item => realIds.Contains(item.Id)).Select(item => item.Id).ToHashSet(StringComparer.Ordinal));

        // The carried names never borrow a real diagnostic's id, or rejoining by id and part would splice
        // two different messages into one.
        var carried = seen.Where(item => !realIds.Contains(item.Id)).ToArray();
        Assert.NotEmpty(carried);
        Assert.Empty(carried.Select(item => item.Id).Intersect(realIds, StringComparer.Ordinal));

        // And every project name is accounted for: shown beside the counts, or inside the carried message.
        var rejoined = string.Concat(carried.OrderBy(item => item.Part).Select(item => item.Message));
        var recovered = first.MissingProjects!
                             .Concat(rejoined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                             .Select(name => name.Split('/', '\\')[^1])
                             .ToHashSet(StringComparer.Ordinal);
        Assert.All(missing, project => Assert.Contains(Path.GetFileName(project), recovered));
    }

    /// <summary>
    /// Diagnostics numerous enough to fill their own envelope, and no missing projects at all. The
    /// envelope used to be given no reserve, so it filled its 8 KB and the wrapper around it — the path,
    /// the counts, the project totals — pushed the result over the limit.
    /// </summary>
    [Fact]
    public void A_full_page_of_diagnostics_leaves_room_for_the_result_around_it()
    {
        var real = WorkspaceSession.Describe(Enumerable.Range(0, 80)
                                                       .Select(index => (Microsoft.CodeAnalysis.WorkspaceDiagnostic)
                                                                   new FakeDiagnostic($"diagnostic {index:D3}: " + new string('m', 180)))
                                                       .ToArray());

        var first = Recall([], real, new PageArguments { Page = 1, PageSize = 50 });

        Assert.True(first.Diagnostics.Items.Count > 0, "the fixture produced no diagnostics to page");
        for (var page = 1; page <= first.Diagnostics.Pages; page++)
        {
            var current = Recall([], real, new PageArguments { Page = page, PageSize = 50 });
            Assert.True(Size(current) <= ResponseEnvelope.MaximumSerializedBytes, $"page {page} was {Size(current)} bytes");
        }
    }

    /// <summary>
    /// Enough missing projects to crowd the shell, and one diagnostic long enough to need fragmenting and
    /// costly enough per character to make the fragments large: Cyrillic escapes to six bytes a character.
    /// <para>This is the pair of budgets meeting. The shell was allowed everything but 2048 bytes while a
    /// fragment could weigh 4096, so no fragment fitted the room the shell left and every page of
    /// diagnostics came back empty with a notice — the message was in the result and reachable from none
    /// of its pages.</para>
    /// </summary>
    [Fact]
    public void A_long_message_beside_many_missing_projects_is_still_reachable()
    {
        var missing = ManyMissing().Take(75).ToArray();
        var message = string.Concat(Enumerable.Repeat("Проект не удалось прочитать. ", 1000)).Substring(0, 1000);
        var real = WorkspaceSession.Describe([new FakeDiagnostic(message)]);

        var first = Recall(missing, real, new PageArguments { Page = 1, PageSize = 50 });
        var seen = new List<WorkspaceDiagnosticResult>();
        for (var page = 1; page <= first.Diagnostics.Pages; page++)
        {
            var current = Recall(missing, real, new PageArguments { Page = page, PageSize = 50 });
            Assert.Equal(first.Diagnostics.Pages, current.Diagnostics.Pages);
            Assert.True(Size(current) <= ResponseEnvelope.MaximumSerializedBytes, $"page {page} was {Size(current)} bytes");
            Assert.NotEmpty(current.Diagnostics.Items);
            seen.AddRange(current.Diagnostics.Items);
        }

        var realIds = real.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(message, string.Concat(seen.Where(item => realIds.Contains(item.Id))
                                                .OrderBy(item => item.Part)
                                                .Select(item => item.Message)));

        var rejoined = string.Concat(seen.Where(item => !realIds.Contains(item.Id)).OrderBy(item => item.Part)
                                         .Select(item => item.Message));
        var recovered = first.MissingProjects!
                             .Concat(rejoined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                             .Select(name => name.Split('/', '\\')[^1])
                             .ToHashSet(StringComparer.Ordinal);
        Assert.All(missing, project => Assert.Contains(Path.GetFileName(project), recovered));
    }

    /// <summary>
    /// The other two lists and the error, which the shell's budget did not bound at all: eight hundred
    /// names in each and a five-thousand-character error could push the result past the limit however hard
    /// missingProjects was trimmed.
    /// </summary>
    [Fact]
    public void Skipped_and_empty_projects_and_a_long_error_are_bounded_and_recoverable()
    {
        var skipped = Names("Skipped");
        var empty = Names("Empty");
        var error = new string('e', 5000);

        var first = Recall([], [], new PageArguments { Page = 1, PageSize = 50 }, skipped, empty, error);

        Assert.True(first.Error!.Length < error.Length, "the error was not truncated");
        Assert.EndsWith("(error truncated)", first.Error, StringComparison.Ordinal);
        Assert.Equal(skipped.Length, first.SkippedProjects!.Count + first.SkippedProjectsHidden);
        Assert.Equal(empty.Length, first.EmptyProjects!.Count + first.EmptyProjectsHidden);

        var seen = new List<WorkspaceDiagnosticResult>();
        for (var page = 1; page <= first.Diagnostics.Pages; page++)
        {
            var current = Recall([], [], new PageArguments { Page = page, PageSize = 50 }, skipped, empty, error);
            Assert.Equal(first.Diagnostics.Pages, current.Diagnostics.Pages);
            Assert.True(Size(current) <= ResponseEnvelope.MaximumSerializedBytes, $"page {page} was {Size(current)} bytes");
            Assert.NotEmpty(current.Diagnostics.Items);
            seen.AddRange(current.Diagnostics.Items);
        }

        // Each list is carried under an id of its own, so rejoining one never splices in the other's names.
        foreach (var (list, names, shown) in new[] { ("skippedProjects", skipped, first.SkippedProjects!),
                                                     ("emptyProjects", empty, first.EmptyProjects!) })
        {
            var id = Assert.Single(seen, item => item.Part == 1 && item.Message.StartsWith(list, StringComparison.Ordinal)).Id;
            var rejoined = string.Concat(seen.Where(item => item.Id == id).OrderBy(item => item.Part).Select(item => item.Message));
            var recovered = shown.Concat(rejoined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                 .Select(name => name.Split(' ')[^1])
                                 .ToHashSet(StringComparer.Ordinal);
            Assert.All(names, project => Assert.Contains(project, recovered));
        }
    }

    /// <summary>
    /// An error long enough to fill the shell on its own, in a script whose characters cost six bytes each
    /// once escaped. Cutting it by character count let 1024 characters of Cyrillic weigh about 6144 bytes,
    /// so the shell stayed over its whole budget with every project list already emptied for nothing, and
    /// no diagnostic fragment could fit beside it — the message was in the result and on none of its pages.
    /// </summary>
    [Fact]
    public void A_long_cyrillic_error_is_cut_by_weight_and_leaves_room_for_the_diagnostics()
    {
        var error = string.Concat(Enumerable.Repeat("Сборка не загрузилась. ", 200))[..3000];
        var message = string.Concat(Enumerable.Repeat("Проект не удалось прочитать. ", 100))[..1000];
        var real = WorkspaceSession.Describe([new FakeDiagnostic(message)]);

        var first = Recall([], real, new PageArguments { Page = 1, PageSize = 50 }, error: error);

        Assert.EndsWith("(error truncated)", first.Error, StringComparison.Ordinal);
        Assert.True(Size(first with { Diagnostics = Empty() }) <= WorkspaceSession.MaximumShellBytes,
                    $"the shell weighed {Size(first with { Diagnostics = Empty() })} bytes");

        var seen = new List<WorkspaceDiagnosticResult>();
        for (var page = 1; page <= first.Diagnostics.Pages; page++)
        {
            var current = Recall([], real, new PageArguments { Page = page, PageSize = 50 }, error: error);
            Assert.Equal(first.Diagnostics.Pages, current.Diagnostics.Pages);
            Assert.True(Size(current) <= ResponseEnvelope.MaximumSerializedBytes, $"page {page} was {Size(current)} bytes");
            Assert.NotEmpty(current.Diagnostics.Items);
            seen.AddRange(current.Diagnostics.Items);
        }

        Assert.Equal(message, string.Concat(seen.OrderBy(item => item.Part).Select(item => item.Message)));
    }

    private static ListEnvelope<WorkspaceDiagnosticResult> Empty() =>
        ResponseEnvelope.Create(Array.Empty<WorkspaceDiagnosticResult>(), null,
                                CacheDetectiveJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult);

    private static string[] Names(string prefix) =>
        Enumerable.Range(0, 800).Select(index => $"{prefix}ProjectDirectory{index:D4}.vcxproj").ToArray();

    /// <summary>One page of a remembered index, through the path the tool answers from.</summary>
    private static IndexSolutionResult Recall(IReadOnlyList<string> missing, IReadOnlyList<WorkspaceDiagnosticResult> diagnostics,
                                              PageArguments? page, IReadOnlyList<string>? skipped = null,
                                              IReadOnlyList<string>? empty = null, string? error = null) =>
        new WorkspaceSession().Recall("App.sln",
                                      new RememberedIndex(true, DateTimeOffset.UnixEpoch, diagnostics, error, missing.Count == 0,
                                                          900, 100, missing, skipped ?? [], empty ?? []),
                                      page);

    private static string[] ManyMissing() =>
        Enumerable.Range(0, 800)
                  .Select(index => $"src/Services/VeryLongProjectDirectoryName{index:D4}/Project{index:D4}.csproj")
                  .ToArray();

    private static int Size(IndexSolutionResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, CacheDetectiveJsonContext.Default.IndexSolutionResult).Length;

    private static int Size(WorkspaceDiagnosticResult fragment) =>
        JsonSerializer.SerializeToUtf8Bytes(fragment, CacheDetectiveJsonContext.Default.WorkspaceDiagnosticResult).Length;

    private sealed class FakeDiagnostic(string message) : WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, message);
}
