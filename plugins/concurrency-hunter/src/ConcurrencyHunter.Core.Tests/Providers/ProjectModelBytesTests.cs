using System.Text;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class ProjectModelBytesTests
{
    private const string VALID = """
        {"schemaVersion":1,"assemblies":["A"],"versions":{"minimum":"1.0.0.0","maximumExclusive":"2.0.0.0"},"models":[{"member":"M:A.Api.First(System.Object)","effects":{"value":["reads-deep"]}},{"member":"M:A.Api.Second(*)","opaque":true}]}
        """;

    [Fact]
    public void Folder_and_byte_reads_return_the_same_entries_and_rejections()
    {
        using var repository = new Repository();
        repository.Write("a.json", VALID);
        repository.Write("nested/b.json", """
            {"schemaVersion":1,"assemblies":["A"],"models":[{"member":"M:A.Api.Bad(System.Object)","opaque":false},{"member":"M:A.Api.Good(System.Object)","effects":{}}]}
            """);

        var folder = ProjectModelFiles.Read(repository.Root);
        var bytes = repository.Files().Select(path => ProjectModelFiles.Read(repository.Relative(path), File.ReadAllBytes(path))).ToArray();

        Assert.Equal(folder.Entries.Select(Describe), bytes.SelectMany(file => file.Entries).Select(Describe));
        Assert.Equal(folder.Rejections, bytes.SelectMany(file => file.Rejections));
        Assert.Equal(folder.NamedPatterns.Order(StringComparer.Ordinal),
                     bytes.SelectMany(file => file.NamedPatterns).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Invalid_file_has_the_same_rejection_when_read_from_bytes()
    {
        using var repository = new Repository();
        var path = repository.Write("invalid.json", "not JSON");
        var relative = repository.Relative(path);

        Assert.Equal(ProjectModelFiles.Read(repository.Root).Rejections,
                     ProjectModelFiles.Read(relative, File.ReadAllBytes(path)).Rejections);
    }

    private static string Describe(ProjectModelEntry entry)
    {
        var version = entry.Versions ?? (new Version(0, 0, 0, 0), new Version(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue));
        var model = new LibraryModel(entry.Member,
            entry.Assemblies.Select(assembly => new SupportedAssemblyVersion(assembly, version.Minimum, version.Maximum)).ToArray(), entry.Effects)
        {
            Result = entry.Result,
            Fates = entry.Fates,
            Stores = entry.Stores,
            Outputs = entry.Outputs,
            Keeps = entry.Keeps
        };
        return $"{entry.Path}:{entry.Position}:{entry.Opaque}:{entry.Versions is null}:{ModelEntryWriter.Write(model).ToJsonString()}";
    }

    private sealed class Repository : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ch-model-bytes-").FullName;
        private string Models => Path.Combine(Root, ".concurrency-hunter", "models");

        public string Write(string name, string text)
        {
            var path = Path.Combine(Models, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        public string[] Files() => Directory.GetFiles(Models, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
