using PlanForgeFlow.Cli;
using PlanForgeFlow.Workflow.State;
using PlanForgeFlow.Infrastructure.Process;

namespace PlanForgeFlow.Infrastructure.Workspace;

/// <summary>Serializes mutations of refs and info/exclude shared by linked worktrees.</summary>
internal sealed class RepositoryRunLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _repositoryKey;
    private readonly HostKind _host;
    private bool _disposed;

    private RepositoryRunLock(FileStream stream, string repositoryKey, HostKind host)
    {
        _stream = stream;
        _repositoryKey = repositoryKey;
        _host = host;
    }

    public static RepositoryRunLock Acquire(RepositoryIdentity repository, HostKind host)
    {
        var root = Environment.GetEnvironmentVariable("FORGE_LOCK_DATA");
        root = string.IsNullOrWhiteSpace(root)
                   ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "plan-forge-flow")
                   : Path.GetFullPath(root);
        var locks = Path.Combine(root, "locks");
        OwnershipGuards.EnsureDirectory(locks);
        var key = RepositoryKey(repository);
        var path = Path.Combine(locks, key + ".lock");
        try
        {
            return new RepositoryRunLock(
                new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough),
                key,
                host);
        }
        catch (IOException)
        {
            throw new CliFailure("state", "another Forge operation owns the repository lock", 3);
        }
    }

    internal void Require(RepositoryIdentity repository, HostKind host)
    {
        if (_disposed || _repositoryKey != RepositoryKey(repository) || _host != host)
            throw new CliFailure("state", "the repository lock token does not cover this operation", 3);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }

    private static string RepositoryKey(RepositoryIdentity repository) =>
        Hashing.Sha256Hex(OperatingSystem.IsWindows() ? repository.GitCommonDir.ToLowerInvariant() : repository.GitCommonDir);
}
