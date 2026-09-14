using System.Security;

namespace ConcurrencyHunter.Runs;

internal enum TargetKind
{
    Resolved,
    Ambiguous,
    Unresolvable,
    TooLong
}

internal sealed record TargetResolution(TargetKind Kind, string? Path, IReadOnlyList<string> Candidates,
                                        string? Reason);

internal static class TargetResolver
{
    private const int MAXIMUM_TARGET_BYTES = 1024;

    internal static TargetResolution Resolve(string? target, Func<string, IEnumerable<string>>? enumerateTopLevelSolutions = null)
    {
        if (target is not null && ResponseBudget.Weight(target) > MAXIMUM_TARGET_BYTES)
            return new TargetResolution(TargetKind.TooLong, null, [], "target path exceeds 1024 bytes");

        if (string.IsNullOrWhiteSpace(target))
        {
            return new TargetResolution(TargetKind.Unresolvable, null, [], "target must be an absolute path");
        }

        try
        {
            if (!Path.IsPathFullyQualified(target))
            {
                return new TargetResolution(TargetKind.Unresolvable, null, [], "target must be an absolute path");
            }

            var fullPath = Path.GetFullPath(target);
            if (ResponseBudget.Weight(fullPath) > MAXIMUM_TARGET_BYTES)
                return new TargetResolution(TargetKind.TooLong, null, [], "target path exceeds 1024 bytes");

            if (File.Exists(fullPath))
            {
                if (IsSupportedFile(fullPath))
                    return new TargetResolution(TargetKind.Resolved, fullPath, [], null);

                return new TargetResolution(TargetKind.Unresolvable, null, [], $"unsupported target file: {fullPath}");
            }

            if (!Directory.Exists(fullPath))
            {
                return new TargetResolution(TargetKind.Unresolvable, null, [], $"target does not exist: {fullPath}");
            }

            var enumerate = enumerateTopLevelSolutions ?? EnumerateTopLevelSolutions;
            var candidates = enumerate(fullPath).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
            if (candidates.Length == 0)
            {
                return new TargetResolution(TargetKind.Unresolvable, null, [], $"no top-level solution found in: {fullPath}");
            }

            if (candidates.Length > 1)
                return new TargetResolution(TargetKind.Ambiguous, null, candidates, null);

            var solutionPath = Path.GetFullPath(Path.Combine(fullPath, candidates[0]));
            if (ResponseBudget.Weight(solutionPath) > MAXIMUM_TARGET_BYTES)
                return new TargetResolution(TargetKind.TooLong, null, [], "target path exceeds 1024 bytes");

            return new TargetResolution(TargetKind.Resolved, solutionPath, [], null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return new TargetResolution(TargetKind.Unresolvable, null, [], $"{error.GetType().Name}: {error.Message}");
        }
    }

    private static IEnumerable<string> EnumerateTopLevelSolutions(string directory) => Directory.EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly)
                                                                                                .Concat(Directory.EnumerateFiles(directory, "*.slnx", SearchOption.TopDirectoryOnly));

    private static bool IsSupportedFile(string path) => Path.GetExtension(path) is { } extension && (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                                                                                                     extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                                                                                                     extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase));
}
