using System.Text;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal static class DurableFiles
{
    public static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("file has no directory");
        EnsureSafeParent(directory);
        if (IsReparseEntry(path)) throw new CliFailure("state", $"refusing to replace a symlinked file: {path}", 3);
        Directory.CreateDirectory(directory);
        EnsureSafeParent(directory);
        if (IsReparseEntry(path)) throw new CliFailure("state", $"refusing to replace a symlinked file: {path}", 3);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);
            }
        File.Move(temp, path, true);
    }

    public static void WriteJson(string path, JsonNode node) => WriteAtomic(path, node.ToJsonString() + "\n");

    public static void WriteJson(string path, ForgeState state) => WriteJson(path, state.ToJson());

    private static void EnsureSafeParent(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", $"refusing to write through a symlinked directory: {current.FullName}", 3);
            try
            {
                if (current.LinkTarget is not null) throw new CliFailure("state", $"refusing to write through a symlinked directory: {current.FullName}", 3);
            }
            catch (PlatformNotSupportedException) { }
        }
    }

    private static bool IsReparseEntry(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }

        try
        {
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null) return true;
        }
        catch (PlatformNotSupportedException) { }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return false;
    }
}
