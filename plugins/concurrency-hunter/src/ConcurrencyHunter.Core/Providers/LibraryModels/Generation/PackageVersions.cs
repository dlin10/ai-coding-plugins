namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>A NuGet package version: up to four numeric parts, a missing part 0, an optional SemVer 2.0 prerelease, build
/// metadata ignored. The generator's own reading of NuGet's rules; no <c>NuGet.*</c> assembly is used.</summary>
/// <param name="Parts">The four numeric parts.</param>
/// <param name="Prerelease">The prerelease labels, empty for a release.</param>
public sealed record PackageVersion(IReadOnlyList<int> Parts, IReadOnlyList<string> Prerelease) : IComparable<PackageVersion>
{
    /// <summary>The folder name NuGet keeps the version under: lowercase, at least three parts, a fourth part only when it is
    /// not 0, metadata dropped.</summary>
    public string Normalized =>
        string.Join(".", Parts.Take(Parts[3] == 0 ? 3 : 4)) +
        (Prerelease.Count == 0 ? "" : "-" + string.Join(".", Prerelease).ToLowerInvariant());

    /// <summary>Reads a version, or answers <c>false</c> when the text is not one: SemVer 2.0's identifiers are non-empty runs of
    /// ASCII letters, digits and <c>-</c>, a numeric prerelease identifier has no leading zero, and build metadata after one
    /// <c>+</c> is checked as strictly as it is ignored afterwards.</summary>
    /// <param name="text">The version text.</param>
    /// <param name="version">The version read.</param>
    public static bool TryParse(string? text, out PackageVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var withMetadata = text.Trim().Split('+', 2);
        if (withMetadata.Length == 2 && !withMetadata[1].Split('.').All(IsIdentifier))
            return false;
        var split = withMetadata[0].Split('-', 2);
        var numbers = split[0].Split('.');
        if (numbers.Length is < 1 or > 4)
            return false;
        var parts = new int[4];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (numbers[i].Length == 0 || !numbers[i].All(char.IsAsciiDigit) || !int.TryParse(numbers[i], out parts[i]))
                return false;
        }

        var prerelease = split.Length == 2 ? split[1].Split('.') : [];
        if (!prerelease.All(label => IsIdentifier(label) && !(label.Length > 1 && label[0] == '0' && label.All(char.IsAsciiDigit))))
            return false;
        version = new PackageVersion(parts, prerelease);
        return true;
    }

    private static bool IsIdentifier(string identifier) => identifier.Length > 0 && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>Orders as NuGet's SemVer 2.0: numeric parts, then a prerelease below its release, prerelease labels compared one by
    /// one (numeric labels numerically and below alphanumeric ones, alphanumeric ones ignoring case), a shorter label list below a
    /// longer one it begins.</summary>
    /// <param name="other">The version compared with.</param>
    public int CompareTo(PackageVersion? other)
    {
        if (other is null)
            return 1;
        for (var i = 0; i < 4; i++)
        {
            var part = Parts[i].CompareTo(other.Parts[i]);
            if (part != 0)
                return part;
        }

        if (Prerelease.Count == 0 || other.Prerelease.Count == 0)
            return (Prerelease.Count == 0).CompareTo(other.Prerelease.Count == 0);
        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            var label = CompareLabel(Prerelease[i], other.Prerelease[i]);
            if (label != 0)
                return label;
        }

        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    /// <summary>Two versions are equal when they order equal.</summary>
    /// <param name="other">The version compared with.</param>
    public bool Equals(PackageVersion? other) => other is not null && CompareTo(other) == 0;

    public override int GetHashCode() => Normalized.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Normalized;

    private static int CompareLabel(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var leftDigits = left.TrimStart('0');
            var rightDigits = right.TrimStart('0');
            return leftDigits.Length != rightDigits.Length
                ? leftDigits.Length.CompareTo(rightDigits.Length)
                : string.CompareOrdinal(leftDigits, rightDigits);
        }

        if (leftNumeric != rightNumeric)
            return leftNumeric ? -1 : 1;
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A NuGet dependency version range: a bare version <c>a</c> (≥ a); <c>[a]</c> (exactly a); <c>[a,b]</c>, <c>[a,b)</c>,
/// <c>(a,b]</c>, <c>(a,b)</c>; <c>[a,)</c>, <c>(a,)</c>; <c>(,b]</c>, <c>(,b)</c>.</summary>
/// <param name="Text">The range as the nuspec wrote it.</param>
/// <param name="Minimum">The lower bound, <c>null</c> for none.</param>
/// <param name="MinimumInclusive">Whether the lower bound is admitted.</param>
/// <param name="Maximum">The upper bound, <c>null</c> for none.</param>
/// <param name="MaximumInclusive">Whether the upper bound is admitted.</param>
public sealed record PackageVersionRange(string Text, PackageVersion? Minimum, bool MinimumInclusive, PackageVersion? Maximum,
                                         bool MaximumInclusive)
{
    /// <summary>Reads a range, or answers <c>false</c> when the text is not one of the forms above: a missing bound takes a round
    /// bracket on its side, so <c>[a,]</c>, <c>[,b]</c>, <c>[,]</c> and <c>(,)</c> are not ranges.</summary>
    /// <param name="text">The range text.</param>
    /// <param name="range">The range read.</param>
    public static bool TryParse(string? text, out PackageVersionRange range)
    {
        range = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        if (trimmed[0] is not ('[' or '('))
        {
            if (!PackageVersion.TryParse(trimmed, out var bare))
                return false;
            range = new PackageVersionRange(trimmed, bare, true, null, false);
            return true;
        }

        if (trimmed.Length < 3 || trimmed[^1] is not (']' or ')'))
            return false;
        var minimumInclusive = trimmed[0] == '[';
        var maximumInclusive = trimmed[^1] == ']';
        var inner = trimmed[1..^1].Split(',');
        if (inner.Length == 1)
        {
            if (!minimumInclusive || !maximumInclusive || !PackageVersion.TryParse(inner[0], out var exact))
                return false;
            range = new PackageVersionRange(trimmed, exact, true, exact, true);
            return true;
        }

        if (inner.Length != 2)
            return false;
        PackageVersion? minimum = null;
        PackageVersion? maximum = null;
        if (inner[0].Trim().Length > 0 && !PackageVersion.TryParse(inner[0], out minimum))
            return false;
        if (inner[1].Trim().Length > 0 && !PackageVersion.TryParse(inner[1], out maximum))
            return false;
        if (minimum is null && maximum is null || minimum is null && minimumInclusive || maximum is null && maximumInclusive)
            return false;
        range = new PackageVersionRange(trimmed, minimum, minimumInclusive, maximum, maximumInclusive);
        return true;
    }

    /// <summary>Whether the range admits a version.</summary>
    /// <param name="version">The version asked about.</param>
    public bool Admits(PackageVersion version)
    {
        if (Minimum is not null)
        {
            var low = version.CompareTo(Minimum);
            if (low < 0 || low == 0 && !MinimumInclusive)
                return false;
        }

        if (Maximum is not null)
        {
            var high = version.CompareTo(Maximum);
            if (high > 0 || high == 0 && !MaximumInclusive)
                return false;
        }

        return true;
    }
}
