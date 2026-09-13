using System.Text.Json;

namespace Common.Mcp;

public static class DiagnosticFragments
{
    /// <summary>
    /// The one budget everything else is derived from: the most the result around the diagnostics may
    /// weigh. What is left of <see cref="ResponseEnvelope.MaximumSerializedBytes"/> after it is what a page
    /// of diagnostics has to live in, and <see cref="DiagnosticFragmentBytes"/> is cut from that same
    /// remainder — so a fragment always fits beside a shell.
    /// <para>The two used to be set apart from each other: the shell was allowed to keep all but 2048
    /// bytes while a fragment could be 4096, so with enough project names no fragment fitted at all and
    /// every page of diagnostics came back empty.</para>
    /// </summary>
    public const int MaximumShellBytes = ResponseEnvelope.MaximumSerializedBytes / 2;

    /// <summary>
    /// What the envelope around a single fragment costs, measured rather than estimated — and measured at
    /// its worst, with the counts and the notice <see cref="ResponseEnvelope"/> writes when it has had to
    /// reduce a page. It is declared before the budget it feeds, because static initialisers run in the
    /// order they are written.
    /// </summary>
    private static readonly int ENVELOPE_OVERHEAD = JsonSerializer.SerializeToUtf8Bytes(new ListEnvelope<WorkspaceDiagnosticResult>(int.MaxValue, int.MaxValue, int.MaxValue, [],
                                                                                            "Page size was reduced to stay under the response limit."),
                                                                                       CommonJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult).Length;

    /// <summary>
    /// How much a fragment of one diagnostic message may weigh once serialized. It is what is left of the
    /// response limit once the heaviest shell and the envelope around the fragment have both been paid
    /// for, so a fragment that passes this fits on a page beside any shell the result can produce. Setting
    /// it independently of the shell's budget is what made every page come back empty: a 4096-byte
    /// fragment cannot go on a page a 6144-byte shell left 2048 bytes of.
    /// <para>The measure is bytes of JSON rather than UTF-16 characters because the two are not the same
    /// size: a Cyrillic message costs two bytes a character before escaping and six after (<c>\uXXXX</c>),
    /// so 1500 characters could reach about 9000 bytes and <see cref="ResponseEnvelope"/> would answer
    /// with an empty page and a notice — no answer at all.</para>
    /// </summary>
    private static readonly int DIAGNOSTIC_FRAGMENT_BYTES =
        ResponseEnvelope.MaximumSerializedBytes - MaximumShellBytes - ENVELOPE_OVERHEAD;

    /// <summary>The largest fragment tried first. Cutting is by bytes, so the character count only bounds
    /// the search.</summary>
    private const int DIAGNOSTIC_FRAGMENT_CHARACTERS = 1500;

    public static IReadOnlyList<WorkspaceDiagnosticResult> Describe(IReadOnlyList<(string Kind, string Message)> diagnostics,
                                                                     int numberedFrom = 0)
    {
        var mapped = new List<WorkspaceDiagnosticResult>();
        for (var index = 0; index < diagnostics.Count; index++)
        {
            var diagnostic = diagnostics[index];
            var id = $"d:{numberedFrom + index + 1}";
            var kind = diagnostic.Kind;
            var fragments = Fragments(diagnostic.Message, id, kind);
            for (var part = 0; part < fragments.Count; part++)
                mapped.Add(new WorkspaceDiagnosticResult(id, kind, fragments[part], part + 1, fragments.Count));
        }

        return mapped;
    }

    /// <summary>
    /// Cuts a message into pieces each of which fits when serialized. A cut never lands between the two
    /// halves of a surrogate pair, which would turn one character into two broken ones and make the
    /// reassembled text differ from the original.
    /// </summary>
    private static IReadOnlyList<string> Fragments(string message, string id, string kind)
    {
        if (message.Length == 0)
            return [string.Empty];

        var fragments = new List<string>();
        var start = 0;
        while (start < message.Length)
        {
            var length = Math.Min(DIAGNOSTIC_FRAGMENT_CHARACTERS, message.Length - start);
            length = ResponseText.WithoutSplitSurrogate(message, start, length);
            while (length > 1 && SerializedSize(message.Substring(start, length), id, kind) > DIAGNOSTIC_FRAGMENT_BYTES)
                length = ResponseText.WithoutSplitSurrogate(message, start, Math.Max(1, length / 2));

            fragments.Add(message.Substring(start, length));
            start += length;
        }

        return fragments;
    }

    private static int SerializedSize(string fragment, string id, string kind) =>
        JsonSerializer.SerializeToUtf8Bytes(new WorkspaceDiagnosticResult(id, kind, fragment, 1, 1),
                                            CommonJsonContext.Default.WorkspaceDiagnosticResult).Length;

    /// <param name="reserve">How much the result carrying this envelope weighs around it. Without it the
    /// envelope fills the whole budget on its own and whatever wraps it pushes the response over.</param>
    public static ListEnvelope<WorkspaceDiagnosticResult> Page(IReadOnlyList<WorkspaceDiagnosticResult> fragments,
                                                                PageArguments? page, int reserve = 0) =>
        ResponseEnvelope.Create(fragments, page, CommonJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult, reserve);
}
