using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;

namespace PlanForgeFlow.Workflow.State;

internal sealed class ForgeStateLock : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private ForgeStateLock(FileStream stream)
    {
        _stream = stream;
    }

    public static ForgeStateLock Acquire(string workspace)
    {
        OwnershipGuards.EnsureSafeDirectory(workspace);
        var root = Environment.GetEnvironmentVariable("FORGE_LOCK_DATA");
        root = string.IsNullOrWhiteSpace(root)
                   ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "plan-forge-flow")
                   : Path.GetFullPath(root);
        var locks = Path.Combine(root, "workspace-locks");
        OwnershipGuards.EnsureDirectory(locks);
        var canonical = RepositoryPaths.CanonicalWorkspaceRoot(workspace);
        var key = Hashing.Sha256Hex(OperatingSystem.IsWindows() ? canonical.ToLowerInvariant() : canonical);
        var path = Path.Combine(locks, key + ".lock");
        EnsureSafeLockPath(path);
        try
        {
            // The lock is the OS handle, not file existence: an abandoned lock file
            // becomes acquirable after a crash when the kernel releases its handle.
            return new ForgeStateLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough));
        }
        catch (IOException)
        {
            throw new CliFailure("state", "another Forge operation owns the state lock", 3);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }

    private static void EnsureSafeLockPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0) throw new CliFailure("state", $"Forge state lock must be a regular file: {path}", 3);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        try
        {
            if (new FileInfo(path).LinkTarget is not null) throw new CliFailure("state", $"Forge state lock must not be a symlink: {path}", 3);
        }
        catch (PlatformNotSupportedException) { }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
