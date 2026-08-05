using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Review;

internal static class ReviewEvidence
{
    public static readonly IReadOnlyList<string> Files = ["pre-existing.patch", "in-run.patch", "untracked-review.patch", "changed-files.txt"];

    public static void EnsureFile(string path)
    {
        if (!File.Exists(path)) throw new CliFailure("state", $"review evidence is missing: {path}", 3);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new CliFailure("state", $"review evidence must be a regular file: {path}", 3);
        try
        {
            if (new FileInfo(path).LinkTarget is not null) throw new CliFailure("state", $"review evidence must not be a symlink: {path}", 3);
        }
        catch (PlatformNotSupportedException) { }
    }

    public static void Verify(string workspace, ReviewManifest manifest)
    {
        var hashes = manifest.EvidenceHashes;
        var actualKeys = hashes.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var expectedKeys = Files.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!actualKeys.SequenceEqual(expectedKeys, StringComparer.Ordinal)) throw new CliFailure("state", "review manifest evidence hash fields are not exact", 3);

        var forge = Path.Combine(workspace, ".forge");
        OwnershipGuards.EnsureSafeDirectory(forge);
        foreach (var name in Files)
        {
            hashes.TryGetValue(name, out var expected);
            if (!Regex.IsMatch(expected ?? string.Empty, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new CliFailure("state", $"review evidence hash is malformed: {name}", 3);
            var path = Path.Combine(forge, name);
            EnsureFile(path);
            if (!string.Equals(Hashing.Sha256File(path), expected, StringComparison.Ordinal)) throw new CliFailure("state", $"review evidence changed after preparation: {name}", 3);
        }

        var manifestPath = Path.Combine(forge, "review-manifest.json");
        EnsureFile(manifestPath);
        try
        {
            var onDisk = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), ForgeJsonContext.Default.ReviewManifest) ?? throw new JsonException("JSON value is null");
            var expected = JsonSerializer.Serialize(manifest, ForgeJsonContext.Default.ReviewManifest);
            var actual = JsonSerializer.Serialize(onDisk, ForgeJsonContext.Default.ReviewManifest);
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new CliFailure("state", "review manifest does not match the state evidence", 3);
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"review manifest is malformed: {error.Message}", 3); }
    }

    public static (string? Reason, long Bytes) Inspect(
        string workspace,
        string relative,
        ISet<string> preExisting,
        ISet<string> stateAllowed,
        ISet<string> sensitiveAllowed,
        ISet<string> untracked,
        IReadOnlyDictionary<string, string> baselineUntracked,
        long aggregateBytes)
    {
        ValidatePath(relative);
        var sensitiveAuthorized = sensitiveAllowed.Contains(relative);
        if (preExisting.Contains(relative) && !stateAllowed.Contains(relative)) return ("pre-existing change requires explicit authorization", 0);
        if (SensitiveInput.IsSensitivePath(relative) && !sensitiveAuthorized) throw new CliFailure("verdict", $"review evidence includes a sensitive path; explicitly allow it with --allow-paths or review authorize-preexisting: {relative}", 2);

        var absolute = Path.GetFullPath(Path.Combine(workspace, relative));
        if (!IsContained(workspace, absolute)) throw new CliFailure("state", $"review path escapes the workspace: {relative}", 3);
        if (!File.Exists(absolute)) return untracked.Contains(relative) || baselineUntracked.ContainsKey(relative) ? ("untracked evidence is unavailable", 0) : (null, 0);
        var attributes = File.GetAttributes(absolute);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", $"review evidence includes a symlinked path: {relative}", 3);
        var length = new FileInfo(absolute).Length;
        if (length > 100 * 1024 || aggregateBytes + length > 1024 * 1024) return ("evidence size bound", 0);
        if (IsBinaryFile(absolute)) return sensitiveAuthorized ? (null, length) : ("binary evidence requires explicit authorization", 0);
        if (SensitiveInput.IsSensitiveContent(File.ReadAllText(absolute)) && !sensitiveAuthorized) throw new CliFailure("verdict", $"review evidence includes withheld sensitive content: {relative}", 2);
        return (null, length);
    }

    public static string[] PathList(string workspace, IReadOnlyList<string> args, string errorMessage)
        => new GitClient(workspace).NullSeparatedPaths(args, errorMessage)
                                    .Select(NormalizePath)
                                    .Distinct(StringComparer.Ordinal)
                                    .ToArray();

    public static List<BaselineEntry> BaselineEntries(string workspace, IEnumerable<string> paths)
    {
        var entries = new List<BaselineEntry>();
        foreach (var relative in paths.OrderBy(path => path, StringComparer.Ordinal))
        {
            var absolute = Path.GetFullPath(Path.Combine(workspace, relative));
            if (!IsContained(workspace, absolute)) throw new CliFailure("state", $"untracked baseline path escapes the workspace: {relative}", 3);
            if (!File.Exists(absolute)) continue;
            if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", $"untracked baseline path is symlinked: {relative}", 3);
            entries.Add(new BaselineEntry(relative, Hashing.Sha256File(absolute)));
        }

        return entries;
    }

    public static string TreeFingerprint(string workspace, ForgeState state)
    {
        var paths = PathList(workspace, ["diff", "--name-only", "-z", "refs/plan-forge/head-base", "--", "."], "could not fingerprint the tracked review tree")
                   .Concat(PathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not fingerprint the untracked review tree"))
                   .Concat(BaselineUntracked(state).Keys)
                   .Distinct(StringComparer.Ordinal)
                   .OrderBy(path => path, StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var relative in paths)
        {
            ValidatePath(relative);
            var absolute = Path.GetFullPath(Path.Combine(workspace, relative));
            builder.Append(relative).Append('\0');
            if (!IsContained(workspace, absolute)) throw new CliFailure("state", $"review fingerprint path escapes the workspace: {relative}", 3);
            if (!File.Exists(absolute))
            {
                builder.Append("<missing>\0");
                continue;
            }

            if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", $"review fingerprint path is symlinked: {relative}", 3);
            builder.Append(Hashing.Sha256File(absolute)).Append('\0');
        }

        return Hashing.Sha256Hex(builder.ToString());
    }

    public static string DiffOutput(string workspace, IReadOnlyList<string> args, IReadOnlyList<string> paths, string errorMessage)
        => new GitClient(workspace).DiffOutput(args, paths, errorMessage);

    public static Dictionary<string, string> BaselineUntracked(ForgeState state)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in state.Baselines.Untracked)
        {
            var path = entry.Path.Replace('\\', '/');
            var hash = entry.Hash;
            if (!string.IsNullOrWhiteSpace(path) && Regex.IsMatch(hash ?? string.Empty, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) result[path] = hash!;
        }

        return result;
    }

    public static string BuildUntrackedEvidence(string workspace, IEnumerable<string> paths, ISet<string> allowed)
    {
        var builder = new StringBuilder();
        foreach (var path in paths.OrderBy(path => path, StringComparer.Ordinal))
        {
            var absolute = Path.GetFullPath(Path.Combine(workspace, path));
            builder.Append(path).Append('\t');
            if (!File.Exists(absolute) || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
            {
                builder.Append("withheld").Append('\n');
                continue;
            }

            var bytes = new FileInfo(absolute).Length;
            if (bytes > 100 * 1024 || SensitiveInput.IsSensitivePath(path) && !allowed.Contains(path))
            {
                builder.Append("withheld\t").Append(Hashing.Sha256File(absolute)).Append('\n');
                continue;
            }

            var raw = File.ReadAllBytes(absolute);
            var rawHash = Hashing.Sha256Hex(raw);
            if (IsBinaryBytes(raw))
            {
                if (!allowed.Contains(path))
                {
                    builder.Append("withheld\t").Append(rawHash).Append('\n');
                    continue;
                }

                builder.Append("binary-base64\t")
                       .Append(rawHash)
                       .Append('\t')
                       .Append(Convert.ToBase64String(raw))
                       .Append('\n');
                continue;
            }

            var content = new UTF8Encoding(false, true).GetString(raw);
            if (SensitiveInput.IsSensitiveContent(content) && !allowed.Contains(path))
            {
                builder.Append("withheld\t").Append(rawHash).Append('\n');
                continue;
            }

            builder.Append("content-base64\t")
                   .Append(rawHash)
                   .Append('\t')
                   .Append(Convert.ToBase64String(raw))
                   .Append('\n');
        }

        return builder.ToString();
    }

    public static bool IsContained(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative is ".." or "" || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) return false;
        for (var current = new DirectoryInfo(Path.GetDirectoryName(fullCandidate)!); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }

        return true;
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        ValidatePath(normalized);
        return normalized;
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || Path.IsPathRooted(path) || path.Contains('\0')) throw new CliFailure("state", "review path is malformed", 3);
        if (path == ".." || path.StartsWith("../", StringComparison.Ordinal)) throw new CliFailure("state", $"review path contains traversal: {path}", 3);
    }

    private static bool IsBinaryFile(string path) => IsBinaryBytes(File.ReadAllBytes(path));

    private static bool IsBinaryBytes(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) return true;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return !bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(text));
        }
        catch (DecoderFallbackException) { return true; }
    }
}
