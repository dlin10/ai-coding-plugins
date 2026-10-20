using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public static class GeneratorFingerprint
{
    public static IReadOnlyList<string> Inputs(string pluginsRoot)
    {
        var root = Path.GetFullPath(pluginsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projects = new[] { "ConcurrencyHunter.Analysis", "ConcurrencyHunter.Core" };
        var inputs = new List<string> { "Directory.Build.props", "Directory.Packages.props" };
        foreach (var project in projects)
        {
            var directory = Path.Combine(root, "concurrency-hunter", "src", project);
            inputs.Add("concurrency-hunter/src/" + project + "/" + project + ".csproj");
            inputs.AddRange(Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Select(path => path.Substring(root.Length + 1).Replace('\\', '/'))
                .Where(path => !path.Split('/').Any(part => part == "bin" || part == "obj")));
        }
        var builtIn = Path.Combine(root, "concurrency-hunter", "src", "ConcurrencyHunter.Core", "Providers", "LibraryModels", "BuiltIn");
        inputs.AddRange(Directory.EnumerateFiles(builtIn, "*.json")
            .Select(path => path.Substring(root.Length + 1).Replace('\\', '/')));
        return inputs.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    public static string Compute(string pluginsRoot)
    {
        using (var stream = new MemoryStream())
        {
            foreach (var path in Inputs(pluginsRoot))
            {
                var name = Encoding.UTF8.GetBytes(path);
                stream.Write(name, 0, name.Length);
                stream.WriteByte(0);
                var content = File.ReadAllBytes(Path.Combine(pluginsRoot, path));
                var length = 0;
                for (var index = 0; index < content.Length; index++)
                {
                    if (content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                        continue;
                    content[length++] = content[index];
                }
                stream.Write(content, 0, length);
                stream.WriteByte(0);
            }
            stream.Position = 0;
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}

#if !GENERATOR_FINGERPRINT_TESTS
public sealed class ComputeGeneratorFingerprint : Microsoft.Build.Utilities.Task
{
    [Microsoft.Build.Framework.Required]
    public string PluginsRoot { get; set; } = "";

    [Microsoft.Build.Framework.Output]
    public string Fingerprint { get; set; } = "";

    public override bool Execute()
    {
        Fingerprint = GeneratorFingerprint.Compute(PluginsRoot);
        return true;
    }
}
#endif
