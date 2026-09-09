using System.Security.Cryptography;
using CacheDetective.Mcp;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>Cache Detective reads a repository and writes one file into it. These take a listing of a
/// temporary workspace root with a content hash per file, index and query through the session, take it
/// again, and compare: anything the session created or changed beyond
/// <c>.cache-detective/workspace.json</c> shows up as a path in the failure.</summary>
public sealed class WorkspaceWriteConfinementTests
{
    /// <summary>
    /// The project is inside the hashed root, which is the arrangement the guarantee is about — indexing a
    /// project that lives somewhere else could not have written into this root whatever the session did.
    /// <para>Exactly one file is more than can be asserted here, and that is measured rather than assumed:
    /// opening the project runs MSBuild's design-time build, which writes its own intermediate output under
    /// the project's <c>obj</c>. Those writes are MSBuild's and not the session's, so the strongest true
    /// statement is this one — outside that one directory exactly one file appeared, and it is the workspace
    /// configuration. The directory is named from the indexed project rather than by skipping any path
    /// segment called <c>obj</c>, so a file the session wrote into some other <c>obj</c> still fails.</para>
    /// </summary>
    [Fact]
    public async Task Outside_msbuilds_intermediate_output_index_and_query_create_only_the_workspace_configuration()
    {
        using var root = new TemporaryDirectory();
        var project = await WriteProjectAsync(root.Path);
        var session = new WorkspaceSession();
        var before = Snapshot(root.Path);

        await session.InitializeAsync(root.Path, [project], null);
        var indexed = await session.IndexSolutionAsync(project);
        await QueryAsync(session);
        var after = Snapshot(root.Path);

        Assert.True(indexed.Succeeded, indexed.Error);
        var changed = Changed(before, after);
        var intermediate = Path.GetRelativePath(root.Path, Path.Combine(Path.GetDirectoryName(project)!, "obj")) +
                           Path.DirectorySeparatorChar;
        var outside = changed.Where(path => !path.StartsWith(intermediate, StringComparison.Ordinal)).ToArray();

        Assert.Equal([Path.Combine(".cache-detective", "workspace.json")], outside);
    }

    /// <summary>And nothing MSBuild wrote is a source file: the two files the fixture wrote are byte for byte
    /// what they were, which needs no exclusion at all.</summary>
    [Fact]
    public async Task Index_and_query_leave_the_indexed_sources_unmodified()
    {
        using var root = new TemporaryDirectory();
        var project = await WriteProjectAsync(root.Path);
        var session = new WorkspaceSession();
        var before = Snapshot(root.Path);

        await session.InitializeAsync(root.Path, [project], null);
        var indexed = await session.IndexSolutionAsync(project);
        await QueryAsync(session);
        var after = Snapshot(root.Path);

        Assert.True(indexed.Succeeded, indexed.Error);
        foreach (var source in new[] { "App.csproj", "Controller.cs" })
        {
            Assert.Equal(before[source], after[source]);
        }
    }

    private static Task QueryAsync(WorkspaceSession session) =>
        session.ReadGraphAsync((graph, configuration) => FindingQueries.FindIssues(graph, configuration?.Budgets,
            new FindingCatalog(), null, null, true, new PageArguments()));

    private static string[] Changed(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        after.Keys.Union(before.Keys, StringComparer.Ordinal)
            .Where(path => !before.TryGetValue(path, out var was) || !after.TryGetValue(path, out var now) || was != now)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyDictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path),
                          path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                          StringComparer.Ordinal);

    private static async Task<string> WriteProjectAsync(string directory)
    {
        var project = Path.Combine(directory, "App.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(directory, "Controller.cs"), """
            public class ControllerBase { }
            public sealed class ProductsController : ControllerBase
            {
                public void Get() => Helper();
                private static void Helper() { }
            }
            """);
        return project;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cache-detective-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
