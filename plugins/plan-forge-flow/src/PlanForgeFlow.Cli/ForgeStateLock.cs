using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed class ForgeStateLock : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
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
        Materializer.EnsureSafeDirectory(forge);
        var path = Path.Combine(forge, "lock");
        EnsureSafeLockPath(path);
        var token = Hashing.Nonce();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(new JsonObject { ["pid"] = Environment.ProcessId, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["token"] = token }.ToJsonString());
                writer.Flush();
                stream.Flush(true);
                if (Environment.GetEnvironmentVariable("FORGE_STATE_LOCK_CRASH_AT") == "after-create") throw new InvalidOperationException("state lock crash injection after-create");
                return new ForgeStateLock(path, token);
            }
            catch (IOException) when (attempt == 0)
            {
                if (!TryReclaimStale(path)) throw;
            }
        }

        throw new CliFailure("state", "another Forge operation owns the state lock", 3);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            var value = JsonNode.Parse(File.ReadAllText(_path))?.AsObject();
            if (value?["token"]?.GetValue<string>() == _token) File.Delete(_path);
        }
        catch { }
    }

    private static bool TryReclaimStale(string path)
    {
        int observedPid;
        long observedTimestamp;
        string observedToken;
        try
        {
            EnsureSafeLockPath(path);
            var value = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            observedTimestamp = value?["ts"]?.GetValue<long>() ?? 0;
            observedPid = value?["pid"]?.GetValue<int>() ?? -1;
            observedToken = value?["token"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrEmpty(observedToken) || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(observedTimestamp) < StaleAfter) return false;
            try
            {
                using var process = Process.GetProcessById(observedPid);
                if (!process.HasExited) return false;
            }
            catch (ArgumentException) { }
        }
        catch (CliFailure) { throw; }
        catch { return false; }

        var claimPath = path + ".recovery-" + Hashing.Nonce();
        try
        {
            EnsureSafeLockPath(path);
            try
            {
                // Rename is the atomic claim. All subsequent checks and deletion target
                // the claimed inode/path, never the pathname that contenders can replace.
                File.Move(path, claimPath);
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return false; }

            EnsureSafeLockPath(claimPath);
            var claimed = JsonNode.Parse(File.ReadAllText(claimPath))?.AsObject();
            var claimedPid = claimed?["pid"]?.GetValue<int>() ?? -1;
            var claimedTimestamp = claimed?["ts"]?.GetValue<long>() ?? 0;
            var claimedToken = claimed?["token"]?.GetValue<string>() ?? string.Empty;
            if (claimedPid != observedPid || claimedTimestamp != observedTimestamp || !string.Equals(claimedToken, observedToken, StringComparison.Ordinal))
                throw new CliFailure("state", "Forge state lock token or PID changed during stale recovery", 3);
            if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(claimedTimestamp) < StaleAfter)
                throw new CliFailure("state", "Forge state lock became fresh during stale recovery", 3);
            try
            {
                using var process = Process.GetProcessById(claimedPid);
                if (!process.HasExited) throw new CliFailure("state", "Forge state lock owner became alive during stale recovery", 3);
            }
            catch (ArgumentException) { }

            // The claimed path is private to this recovery attempt, so this cannot
            // unlink a newer lock created at the original path.
            File.Delete(claimPath);
            return true;
        }
        catch (CliFailure) { throw; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
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