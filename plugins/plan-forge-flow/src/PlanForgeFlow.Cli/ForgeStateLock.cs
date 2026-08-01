using System.Text;

namespace PlanForgeFlow;

internal sealed class ForgeStateLock : IDisposable
{
    private readonly string _path;
    private readonly string _token;
    private bool _disposed;

    private ForgeStateLock(string path, string token)
    {
        _path = path;
        _token = token;
    }

    public static ForgeStateLock Acquire(string workspace)
    {
        var forge = Path.Combine(workspace, ".forge");
        Materializer.EnsureSafeDirectory(forge);
        Directory.CreateDirectory(forge);
        Materializer.EnsureSafeDirectory(forge);
        var path = Path.Combine(forge, "lock");
        EnsureSafeLockPath(path);
        var token = Hashing.Nonce();
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(token);
            writer.Flush();
            stream.Flush(true);
            return new ForgeStateLock(path, token);
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
        try
        {
            if (File.ReadAllText(_path) == _token) File.Delete(_path);
        }
        catch { }
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
