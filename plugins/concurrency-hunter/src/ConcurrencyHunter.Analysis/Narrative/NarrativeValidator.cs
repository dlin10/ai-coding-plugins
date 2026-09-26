using System.Text;
using System.Text.RegularExpressions;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Narrative;

public static class NarrativeValidator
{
    public const int MAXIMUM_BYTES = 8192;

    private static readonly Regex CITATION_PATTERN = new(@"\[E:([A-Za-z0-9.]+)\]");
    private static readonly Regex REMEDIATION_HEADING_PATTERN = new(@"^#{1,6}\s+Remediation\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex HEADING_PATTERN = new(@"^#{1,6}\s");
    private static readonly Regex RECOMMENDATION_PATTERN = new(@"^(?:[-*]|\d+\.)\s+\S");
    private static readonly Regex CHECK_PATTERN = new(@"^\s+(?:[-*]|\d+\.)\s+Check:\s*\S", RegexOptions.IgnoreCase);
    private static readonly Regex BACKTICK_PATTERN = new(@"`([^`\r\n]+)`");
    private static readonly Regex UNBACKTICKED_LOCATION_PATTERN = new(@"\.cs\b", RegexOptions.IgnoreCase);
    private static readonly Regex LOCATION_PATTERN = new(@"^(.+\.cs)(?::(\d+))?$", RegexOptions.IgnoreCase);
    private static readonly Regex IDENTIFIER_PATTERN = new(@"^@?[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}\p{Cf}]*" +
                                                           @"(?:\.@?[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}\p{Cf}]*)*" +
                                                           @"(?:\([^()]*\))?$");
    private static readonly Regex VERBATIM_IDENTIFIER_PREFIX_PATTERN = new(@"(?<![\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}\p{Cf}])@(?=[\p{L}\p{Nl}_])");

    private static readonly HashSet<string> SYNCHRONIZATION_VOCABULARY = new(StringComparer.Ordinal)
    {
        "lock",
        "Monitor",
        "Interlocked",
        "Volatile",
        "volatile",
        "SemaphoreSlim",
        "ReaderWriterLockSlim",
        "Lock",
        "Mutex",
        "ConcurrentDictionary",
        "ConcurrentQueue",
        "ConcurrentBag",
        "ConcurrentStack",
        "ImmutableInterlocked",
        "ThreadLocal",
        "AsyncLocal",
        "readonly",
        "static",
        "const",
        "async",
        "await",
        "Task",
        // The collections a finding on a collection is about, so that a narrative may say which kind it is and that swapping
        // one for the other does not answer a compound operation (ADR 0010).
        "Dictionary",
        "List",
        // The four collections phase 5b adds to that table, and the node of a linked list, which is a cell of it (ADR 0010).
        "HashSet",
        "Queue",
        "Stack",
        "LinkedList",
        "LinkedListNode",
        // The protection verdicts a narrative may quote. The two hyphenated ones, different-identity and incompatible-mode,
        // are no identifiers at all, so they never reach this check.
        "unprotected",
        "partial",
        "sufficient"
    };

    public static NarrativeVerdict Validate(string? text, NarrativeScope scope)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new NarrativeVerdict(false, ["empty"]);

        var reasons = new List<string>();
        var seenReasons = new HashSet<string>(StringComparer.Ordinal);
        void AddReason(string reason)
        {
            if (seenReasons.Add(reason))
                reasons.Add(reason);
        }

        if (Encoding.UTF8.GetByteCount(text) > MAXIMUM_BYTES)
            AddReason("sizeExceeded");

        ValidateCitations(text, scope, AddReason);
        ValidateRemediation(text, scope, AddReason);

        var backticks = BACKTICK_PATTERN.Matches(text).ToArray();
        ValidateUnbacktickedLocations(text, backticks, AddReason);
        ValidateLocations(backticks, scope, AddReason);
        ValidateIdentifiers(backticks, scope, AddReason);

        return new NarrativeVerdict(reasons.Count == 0, reasons.ToArray());
    }

    private static void ValidateCitations(string text, NarrativeScope scope, Action<string> addReason)
    {
        var citations = CITATION_PATTERN.Matches(text).ToArray();
        if (!scope.IsSummary && citations.Length == 0)
            addReason("noCitation");

        var evidenceIds = scope.Findings.SelectMany(finding => finding.Evidence)
                               .Select(evidence => evidence.Id)
                               .ToHashSet(StringComparer.Ordinal);
        foreach (var citation in citations)
        {
            var id = citation.Groups[1].Value;
            if (!evidenceIds.Contains(id))
                addReason($"unknownEvidence:{id}");
        }
        var citedIds = citations.Select(citation => citation.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        foreach (var findingId in scope.FindingsToCite)
        {
            var finding = scope.Findings.Single(item => item.FindingId == findingId);
            if (!finding.Evidence.Any(evidence => citedIds.Contains(evidence.Id)))
                addReason($"uncitedFinding:{findingId}");
        }
    }

    private static void ValidateRemediation(string text, NarrativeScope scope, Action<string> addReason)
    {
        var lines = Regex.Split(text, "\r\n|\r|\n");
        var sectionStart = Array.FindIndex(lines, line => REMEDIATION_HEADING_PATTERN.IsMatch(line));
        if (sectionStart < 0)
        {
            if (!scope.IsSummary)
                addReason("missingRemediation");
            return;
        }

        var sectionEnd = Array.FindIndex(lines, sectionStart + 1, line => HEADING_PATTERN.IsMatch(line));
        if (sectionEnd < 0)
            sectionEnd = lines.Length;

        var starts = Enumerable.Range(sectionStart + 1, sectionEnd - sectionStart - 1)
                               .Where(index => RECOMMENDATION_PATTERN.IsMatch(lines[index]))
                               .ToArray();
        if (!scope.IsSummary && starts.Length == 0)
            addReason("missingRemediation");

        for (var recommendationIndex = 0; recommendationIndex < starts.Length; recommendationIndex++)
        {
            var start = starts[recommendationIndex];
            var end = recommendationIndex + 1 < starts.Length ? starts[recommendationIndex + 1] : sectionEnd;
            var recommendationLines = lines[start..end];
            var number = recommendationIndex + 1;
            if (!recommendationLines.Any(line => line.Contains("verify manually", StringComparison.OrdinalIgnoreCase)))
                addReason($"recommendationWithoutVerifyManually:{number}");
            if (!recommendationLines.Any(line => CHECK_PATTERN.IsMatch(line)))
                addReason($"recommendationWithoutCheck:{number}");
        }
    }

    private static void ValidateUnbacktickedLocations(string text, IReadOnlyList<Match> backticks,
                                                       Action<string> addReason)
    {
        var remaining = text.ToCharArray();
        foreach (var backtick in backticks)
            Array.Fill(remaining, ' ', backtick.Index, backtick.Length);

        var withoutBackticks = new string(remaining);
        foreach (Match location in UNBACKTICKED_LOCATION_PATTERN.Matches(withoutBackticks))
        {
            var start = location.Index;
            while (start > 0 && !char.IsWhiteSpace(withoutBackticks[start - 1]))
                start--;
            var end = location.Index + location.Length;
            while (end < withoutBackticks.Length && !char.IsWhiteSpace(withoutBackticks[end]))
                end++;
            addReason($"unbacktickedLocation:{withoutBackticks[start..end]}");
        }
    }

    private static void ValidateLocations(IReadOnlyList<Match> backticks, NarrativeScope scope, Action<string> addReason)
    {
        var sources = scope.Findings.SelectMany(finding => new[] { finding.AccessA, finding.AccessB })
                           .SelectMany(access => access.ReadSources.Select(read => read.Source).Prepend(access.Source))
                           .Concat(scope.Findings.SelectMany(finding => finding.Evidence).Select(item => item.Source).OfType<SourceSpan>())
                           .ToArray();
        foreach (var backtick in backticks)
        {
            var quoted = backtick.Groups[1].Value.Trim();
            var match = LOCATION_PATTERN.Match(quoted);
            if (!match.Success)
                continue;

            var hasLine = match.Groups[2].Success;
            var line = 0;
            var lineIsValid = !hasLine || int.TryParse(match.Groups[2].Value, out line);
            var accepted = lineIsValid && sources.Any(source =>
                IsPathSuffix(match.Groups[1].Value, source.Path) &&
                (!hasLine || line >= source.StartLine && line <= source.EndLine));
            if (!accepted)
                addReason($"inventedLocation:{quoted}");
        }
    }

    private static void ValidateIdentifiers(IReadOnlyList<Match> backticks, NarrativeScope scope,
                                            Action<string> addReason)
    {
        var evidenceIds = scope.Findings.SelectMany(finding => finding.Evidence)
                               .Select(evidence => evidence.Id)
                               .ToHashSet(StringComparer.Ordinal);
        var accesses = scope.Findings.SelectMany(finding => new[] { finding.AccessA, finding.AccessB }).ToArray();
        var symbols = accesses.SelectMany(access => new[] { access.Symbol, access.Root.Symbol, access.PathRoot.Symbol }
                                  .Concat(access.ReadSources.Select(read => read.Symbol))
                                  .Concat(access.ReadSources.SelectMany(read => read.CodeFlow).Concat(access.CodeFlow).Select(CalleeSymbol).OfType<string>()))
                              .ToArray();
        var exactSymbols = symbols.Select(NormalizeVerbatimIdentifiers)
                                  .ToHashSet(StringComparer.Ordinal);
        var suffixTargets = symbols.Select(symbol => WithoutParameters(NormalizeVerbatimIdentifiers(symbol)))
                                    .Concat(scope.Findings.SelectMany(ResourceTargets))
                                    .Concat(accesses.SelectMany(ProtectionTargets))
                                    .Concat(scope.Findings.SelectMany(finding => finding.GapCallees).Select(GapTarget).OfType<string>())
                                    .Distinct(StringComparer.Ordinal)
                                    .ToArray();

        foreach (var backtick in backticks)
        {
            var quoted = backtick.Groups[1].Value.Trim();
            if (LOCATION_PATTERN.IsMatch(quoted) || !IDENTIFIER_PATTERN.IsMatch(quoted))
                continue;

            var normalized = NormalizeVerbatimIdentifiers(quoted);
            var withoutParameters = WithoutParameters(normalized);
            var firstSegmentEnd = normalized.IndexOfAny(['.', '(']);
            var firstSegment = firstSegmentEnd < 0 ? normalized : normalized[..firstSegmentEnd];
            var accepted = exactSymbols.Contains(normalized) ||
                           suffixTargets.Any(target => IsDotSuffix(withoutParameters, target)) ||
                           evidenceIds.Contains(normalized) ||
                           Regex.IsMatch(normalized, @"^DCA100[1-4]$") ||
                           SYNCHRONIZATION_VOCABULARY.Contains(firstSegment);
            if (!accepted)
                addReason($"inventedSymbol:{quoted}");
        }
    }

    /// <summary>What a narrative may name of the resource: its region's type, and that type with each field of the access
    /// path. A cell's path ends with the selector, which names no member, so the field before it is what a reader writes
    /// (TD-043, ADR 0010).</summary>
    private static IEnumerable<string> ResourceTargets(Finding finding)
    {
        var regionType = NormalizeVerbatimIdentifiers(RegionType(finding.Resource.Region));
        yield return regionType;
        foreach (var segment in finding.Resource.AccessPath.Where(segment => IDENTIFIER_PATTERN.IsMatch(segment)))
            yield return $"{regionType}.{NormalizeVerbatimIdentifiers(segment)}";
    }

    // A held protection name is a region or member, optionally "<region> as <service type>" and a trailing
    // parenthesized note; a receiver lock reads "this <type>". Each named type or member is a target.
    private static IEnumerable<string> ProtectionTargets(Access access) =>
        access.HeldProtection.SelectMany(protection =>
        {
            var note = protection.IndexOf(" (", StringComparison.Ordinal);
            var name = note < 0 ? protection : protection[..note];
            if (name.StartsWith("this ", StringComparison.Ordinal))
                name = name["this ".Length..];
            return name.Split(" as ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(part => NormalizeVerbatimIdentifiers(RegionType(part)));
        });

    /// <summary>What a narrative may name of a semantic gap's callee: its member without type arguments or parameters, such as
    /// <c>CollectionsMarshal.SetCount</c> for <c>System.Runtime.InteropServices.CollectionsMarshal.SetCount&lt;T&gt;(List&lt;string&gt;, int)</c>.
    /// A <c>dynamic</c> operation's callee and a constructor name no member of that form and give nothing.</summary>
    private static string? GapTarget(string callee)
    {
        var member = WithoutParameters(callee);
        while (Regex.IsMatch(member, "<[^<>]*>"))
            member = Regex.Replace(member, "<[^<>]*>", string.Empty);
        member = NormalizeVerbatimIdentifiers(member);
        return IDENTIFIER_PATTERN.IsMatch(member) ? member : null;
    }

    /// <summary>The callee of a <c>call</c> step: <c>calls M(…)</c>, optionally followed by <c> on &lt;receiver&gt;</c>.</summary>
    private static string? CalleeSymbol(CodeFlowStep step)
    {
        if (step.Kind != "call" || !step.Text.StartsWith("calls ", StringComparison.Ordinal))
            return null;
        var callee = step.Text["calls ".Length..];
        var receiver = callee.IndexOf(" on ", StringComparison.Ordinal);
        return receiver < 0 ? callee : callee[..receiver];
    }

    /// <summary>A region's type: <c>static:T</c>, <c>di:T@Lifetime</c> and <c>alloc:M#T</c> (with or without a trailing
    /// <c>#n</c>) all give <c>T</c>; the creating method of an <c>alloc:</c> region is not a type.</summary>
    private static string RegionType(string region)
    {
        if (region.StartsWith("static:", StringComparison.Ordinal))
            return region["static:".Length..];
        if (region.StartsWith("alloc:", StringComparison.Ordinal))
        {
            var site = region.IndexOf('#');
            if (site < 0)
                return region;
            return Regex.Replace(region[(site + 1)..], @"#\d+$", string.Empty);
        }
        if (!region.StartsWith("di:", StringComparison.Ordinal))
            return region;

        var type = region["di:".Length..];
        var lifetime = type.LastIndexOf('@');
        return lifetime < 0 ? type : type[..lifetime];
    }

    private static bool IsPathSuffix(string narrativePath, string evidencePath)
    {
        var narrativeSegments = PathSegments(narrativePath);
        var evidenceSegments = PathSegments(evidencePath);
        if (narrativeSegments.Length == 0 || narrativeSegments.Length > evidenceSegments.Length)
            return false;

        var offset = evidenceSegments.Length - narrativeSegments.Length;
        return narrativeSegments.Select((segment, index) => (segment, index))
                                .All(item => string.Equals(
                                    item.segment,
                                    evidenceSegments[offset + item.index],
                                    StringComparison.OrdinalIgnoreCase));
    }

    private static string[] PathSegments(string path) => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsDotSuffix(string candidate, string target)
    {
        var candidateSegments = candidate.Split('.');
        var targetSegments = target.Split('.');
        if (candidateSegments.Length > targetSegments.Length)
            return false;

        var offset = targetSegments.Length - candidateSegments.Length;
        return candidateSegments.Select((segment, index) => (segment, index))
                                .All(item => string.Equals(
                                    item.segment,
                                    targetSegments[offset + item.index],
                                    StringComparison.Ordinal));
    }

    private static string WithoutParameters(string value)
    {
        var parenthesis = value.IndexOf('(');
        return parenthesis < 0 ? value : value[..parenthesis];
    }

    private static string NormalizeVerbatimIdentifiers(string value) => VERBATIM_IDENTIFIER_PREFIX_PATTERN.Replace(value, string.Empty);
}
