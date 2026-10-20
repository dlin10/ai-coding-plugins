using ConcurrencyHunter.Providers.LibraryModels;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class GeneratorVersionTests
{
    private const string Analysis = "concurrency-hunter/src/ConcurrencyHunter.Analysis/";
    private const string Core = "concurrency-hunter/src/ConcurrencyHunter.Core/";
    private const string BuiltIn = Core + "Providers/LibraryModels/BuiltIn/";

    [Fact]
    public void Built_fingerprint_matches_the_sources()
    {
        var pluginsRoot = Path.GetFullPath("../../../../../../", AppContext.BaseDirectory);
        Assert.Equal(GeneratorVersion.Fingerprint, GeneratorFingerprint.Compute(pluginsRoot));
    }

    [Fact]
    public void Fingerprint_is_64_lowercase_hex() => Assert.Matches("^[0-9a-f]{64}$", GeneratorVersion.Fingerprint);

    [Theory]
    [InlineData("analysis-source", true)]
    [InlineData("core-source", true)]
    [InlineData("add-source", true)]
    [InlineData("delete-source", true)]
    [InlineData("rename-source", true)]
    [InlineData("built-in-json", true)]
    [InlineData("analysis-project", true)]
    [InlineData("core-project", true)]
    [InlineData("build-properties", true)]
    [InlineData("package-properties", true)]
    [InlineData("line-endings", false)]
    [InlineData("bin", false)]
    [InlineData("obj", false)]
    [InlineData("nested-bin", false)]
    [InlineData("nested-obj", false)]
    [InlineData("generated-version", false)]
    [InlineData("other-json", false)]
    [InlineData("nested-json", false)]
    [InlineData("other-project", false)]
    [InlineData("unchanged", false)]
    public void Fingerprint_moves_with_its_inputs_and_only_with_them(string change, bool moves)
    {
        using var tree = new Tree();
        var inputs = new[]
        {
            Analysis + "First.cs", Analysis + "Nested/Second.cs", Analysis + "Nested/Third.cs",
            Core + "First.cs", Core + "Nested/Second.cs", Core + "Nested/Third.cs",
            BuiltIn + "first.json", BuiltIn + "second.json", BuiltIn + "third.json",
            Analysis + "ConcurrencyHunter.Analysis.csproj", Core + "ConcurrencyHunter.Core.csproj",
            "Directory.Build.props", "Directory.Packages.props"
        };
        var excluded = new[]
        {
            Analysis + "bin/First.cs", Core + "bin/Second.cs",
            Analysis + "obj/First.cs", Core + "obj/Second.cs", Core + "obj/Release/net10.0/GeneratorVersion.g.cs",
            Analysis + "Nested/bin/First.cs", Core + "Nested/bin/Second.cs",
            Analysis + "Nested/obj/First.cs", Core + "Nested/obj/Second.cs",
            Core + "first.json", Core + "Providers/LibraryModels/second.json", BuiltIn + "Nested/third.json",
            "other/src/First.cs", "other/src/Second.cs"
        };
        foreach (var path in inputs.Concat(excluded))
            tree.Write(path, "first\r\nsecond\r\n");

        Assert.Equal(inputs.Order(StringComparer.Ordinal), GeneratorFingerprint.Inputs(tree.Root));
        var before = GeneratorFingerprint.Compute(tree.Root);
        switch (change)
        {
            case "analysis-source": tree.Write(Analysis + "Nested/Second.cs", "changed"); break;
            case "core-source": tree.Write(Core + "Nested/Second.cs", "changed"); break;
            case "add-source": tree.Write(Core + "Nested/Added.cs", "added"); break;
            case "delete-source": File.Delete(tree.PathOf(Analysis + "First.cs")); break;
            case "rename-source": File.Move(tree.PathOf(Core + "First.cs"), tree.PathOf(Core + "Renamed.cs")); break;
            case "built-in-json": tree.Write(BuiltIn + "second.json", "changed"); break;
            case "analysis-project": tree.Write(Analysis + "ConcurrencyHunter.Analysis.csproj", "changed"); break;
            case "core-project": tree.Write(Core + "ConcurrencyHunter.Core.csproj", "changed"); break;
            case "build-properties": tree.Write("Directory.Build.props", "changed"); break;
            case "package-properties": tree.Write("Directory.Packages.props", "changed"); break;
            case "line-endings":
                foreach (var path in inputs)
                    tree.Write(path, File.ReadAllText(tree.PathOf(path)).Replace("\r\n", "\n"));
                break;
            case "bin": tree.Write(Analysis + "bin/First.cs", "changed"); break;
            case "obj": tree.Write(Core + "obj/Second.cs", "changed"); break;
            case "nested-bin": tree.Write(Core + "Nested/bin/Second.cs", "changed"); break;
            case "nested-obj": tree.Write(Analysis + "Nested/obj/First.cs", "changed"); break;
            case "generated-version": tree.Write(Core + "obj/Release/net10.0/GeneratorVersion.g.cs", "changed"); break;
            case "other-json": tree.Write(Core + "Providers/LibraryModels/second.json", "changed"); break;
            case "nested-json": tree.Write(BuiltIn + "Nested/third.json", "changed"); break;
            case "other-project": tree.Write("other/src/First.cs", "changed"); break;
            case "unchanged": break;
            default: throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown input change.");
        }

        var after = GeneratorFingerprint.Compute(tree.Root);
        Assert.Equal(moves, before != after);
    }

    private sealed class Tree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ch-generator-version-").FullName;

        public string PathOf(string path) => Path.Combine(Root, path);

        public void Write(string path, string content)
        {
            var fullPath = PathOf(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
