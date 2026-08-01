namespace PlanForgeFlow;

internal static class OwnershipGuards
{
    private const string GeneratedAgentMarker = "# plan-forge-flow:generated";

    public static void EnsureSafeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", $"refusing to materialize through a symlinked directory: {current.FullName}", 3);
            try
            {
                if (current.LinkTarget is not null) throw new CliFailure("state", $"refusing to materialize through a symlinked directory: {current.FullName}", 3);
            }
            catch (PlatformNotSupportedException) { }
        }
    }

    public static void EnsureDirectory(string path)
    {
        EnsureSafeDirectory(path);
        Directory.CreateDirectory(path);
        EnsureSafeDirectory(path);
    }

    public static void EnsureRegularFile(string path, string label)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new CliFailure("state", $"{label} must be a regular file: {path}", 3);
            if (new FileInfo(path).LinkTarget is not null) throw new CliFailure("state", $"{label} must not be a symlink: {path}", 3);
        }
        catch (PlatformNotSupportedException) { }
        catch (FileNotFoundException) { throw new CliFailure("state", $"{label} disappeared: {path}", 3); }
        catch (DirectoryNotFoundException) { throw new CliFailure("state", $"{label} disappeared: {path}", 3); }
    }

    public static void EnsureOwnedArtifact(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new CliFailure("state", $"refusing to overwrite a symlinked or non-file artifact: {path}", 3);
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if (File.ReadLines(path).FirstOrDefault() != CanonicalText.OwnedMarker) throw new CliFailure("state", $"existing artifact is not Forge-owned: {path}", 3);
    }

    public static void EnsureOwnedForgeFile(string path)
        => EnsureRegularFile(path, "Forge-owned cleanup target");

    public static bool IsOwnedAgentFile(string path)
    {
        EnsureOwnedForgeFile(path);
        return File.ReadLines(path).FirstOrDefault() == GeneratedAgentMarker;
    }

    public static void EnsureOwnedAgentFile(string path)
    {
        if (!IsOwnedAgentFile(path)) throw new CliFailure("state", $"refusing to overwrite a foreign generated agent: {path}", 3);
    }
}
