using System.Text;
using CacheDetective.Mcp;
using CacheDetective.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Xunit;

namespace CacheDetective.Tests;

public sealed class MsBuildLoadTests
{
    [Fact]
    public async Task MsBuildLoadReturnsProjectsAndWorkspaceDiagnostics()
    {
        var solutionPath = FindRepositoryFile("plugins", "plan-forge-flow", "src",
            "PlanForgeFlow.sln");
        var loader = new MsBuildSolutionLoader();

        using (var loaded = await loader.LoadAsync(solutionPath))
        {
            Assert.NotEmpty(loaded.Solution.Projects);
            Assert.NotNull(await loaded.Solution.Projects.First().GetCompilationAsync());
            Assert.All(loaded.Diagnostics, diagnostic => Assert.False(string.IsNullOrWhiteSpace(
                diagnostic.Message)));
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"cache-detective-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var projectPath = Path.Combine(temporaryDirectory, "Diagnostic.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="Missing.csproj" />
                  </ItemGroup>
                </Project>
                """);

            using var diagnosticLoad = await loader.LoadAsync(projectPath);
            Assert.NotEmpty(diagnosticLoad.Solution.Projects);
            Assert.NotEmpty(diagnosticLoad.Diagnostics);
            Assert.All(diagnosticLoad.Diagnostics, diagnostic => Assert.False(
                string.IsNullOrWhiteSpace(diagnostic.Message)));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>The fixture's second project promotes MSBuild's own warnings to errors and emits one
    /// during a design-time build. Both projects arrive with a compilation, and the build that produced
    /// the second one ran with the loader's settings rather than the project's.</summary>
    [Fact]
    public async Task The_fixture_solution_loads_whole()
    {
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(FixtureSolution);

        Assert.Equal(["Plain", "Strict"], loaded.Solution.Projects.Select(project => project.Name).Order(StringComparer.Ordinal));
        Assert.All(loaded.Solution.Projects, project => Assert.NotEmpty(project.Documents));
        Assert.Contains(loaded.Diagnostics,
                        diagnostic => diagnostic.Message.Contains("MSBuildTreatWarningsAsErrors=false", StringComparison.Ordinal) &&
                                      diagnostic.Message.Contains("NuGetAudit=false", StringComparison.Ordinal));
    }

    /// <summary>The same solution opened the way the loader used to open it. The project's own
    /// warnings-as-errors is in force, which is the setting the loader exists to overrule.</summary>
    [Fact]
    public async Task Without_the_properties_the_strict_project_keeps_its_warnings_as_errors()
    {
        var (projects, diagnostics) = await OpenWithoutPropertiesAsync();

        Assert.Equal(["Plain", "Strict"], projects);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("MSBuildTreatWarningsAsErrors=true", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Message.Contains("MSBuildTreatWarningsAsErrors=false", StringComparison.Ordinal));
    }

    /// <summary>
    /// The case coverage exists to catch: a project with no documents that a Failure diagnostic names. It
    /// gives the graph nothing, and counting it as opened made loadComplete claim the scan was whole while
    /// a whole project was absent from it.
    /// <para>The failure is put in by hand because no fixture here produces both halves at once. The
    /// obvious candidate — WarningsAsErrors\Strict opened without the loader's global properties, which is
    /// how its design-time build is made to fail — still arrives with 4 documents (Strict.cs and three
    /// generated ones), so a failed build does not by itself empty a project. The zero-document half comes
    /// from the Bare project below, and it builds cleanly. Pairing the two here is what poses the case the
    /// rule is about.</para>
    /// </summary>
    [Fact]
    public async Task A_listed_project_without_documents_after_a_failed_build_is_missing()
    {
        using var temporary = new TemporaryTree();
        var solutionPath = await WriteBareAndFullAsync(temporary);
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath);
        var bare = loaded.Solution.Projects.Single(project => project.Name == "Bare");
        Assert.Empty(bare.Documents);

        var failure = new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure,
                                              $"Msbuild failed when processing the file '{bare.FilePath}'.");
        var coverage = MsBuildSolutionLoader.CoverageFor(solutionPath, loaded.Solution, [failure]);

        Assert.False(coverage.LoadComplete, "a project that gave the graph nothing was counted as loaded");
        Assert.Contains("Bare.csproj", coverage.MissingProjects);
        Assert.DoesNotContain("Full.csproj", coverage.MissingProjects);
        Assert.DoesNotContain("Bare.csproj", coverage.EmptyProjects);
    }

    /// <summary>
    /// The other half of the rule. A project with no documents that nothing failed on is not missing — it
    /// is legitimately empty, and the scan is still complete — but a reader is owed the fact that the graph
    /// got nothing from it.
    /// </summary>
    [Fact]
    public async Task A_listed_project_without_documents_and_without_a_failure_is_empty_not_missing()
    {
        using var temporary = new TemporaryTree();
        var solutionPath = await WriteBareAndFullAsync(temporary);

        using var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath);

        Assert.True(loaded.Coverage.LoadComplete,
                    $"missing: {string.Join(", ", loaded.Coverage.MissingProjects)}");
        Assert.Contains("Bare.csproj", loaded.Coverage.EmptyProjects);
        Assert.DoesNotContain("Bare.csproj", loaded.Coverage.MissingProjects);
        Assert.DoesNotContain("Full.csproj", loaded.Coverage.EmptyProjects);
    }

    /// <summary>
    /// A solution of two projects, one ordinary and one that opens cleanly with no documents at all: the
    /// default globbing is switched off, there is no source file to name explicitly, and the generated
    /// assembly-info files — documents like any other, and the reason switching globbing off alone leaves
    /// one behind — are switched off too.
    /// </summary>
    private static async Task<string> WriteBareAndFullAsync(TemporaryTree temporary)
    {
        await temporary.WriteAsync("Full/Full.csproj", Project());
        await temporary.WriteAsync("Full/Full.cs", "public class Full { }");
        await temporary.WriteAsync("Bare/Bare.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                <EnableNETAnalyzers>false</EnableNETAnalyzers>
              </PropertyGroup>
            </Project>
            """);
        return await temporary.WriteAsync("Both.slnx",
            "<Solution><Project Path=\"Full/Full.csproj\" /><Project Path=\"Bare/Bare.csproj\" /></Solution>");
    }

    [Fact]
    public async Task Without_the_properties_the_diagnostics_name_the_project_they_came_from()
    {
        var (_, diagnostics) = await OpenWithoutPropertiesAsync();

        var failure = Assert.Single(diagnostics, diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure);
        Assert.Contains("Strict.csproj", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Cache Detective fixture", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Every failure MSBuild reports for a project arrives as a diagnostic rather than a throw, so
    /// this exercises the carrier itself on diagnostics a real load produced: what a throw must not lose is
    /// the account of how far the load got, and disposing the workspace is what used to take it.</summary>
    /// <summary>
    /// A <c>global.json</c> pinning an SDK that is not installed. The pin is resolved through the MSBuild
    /// locator, which cannot satisfy it and throws, so this drives the real <c>catch</c> in
    /// <see cref="MsBuildSolutionLoader.LoadAsync"/> rather than constructing its exception by hand.
    /// </summary>
    [Fact]
    public async Task A_load_that_cannot_resolve_the_pinned_sdk_throws_and_carries_what_it_had()
    {
        using var temporary = new TemporaryTree();
        await temporary.WriteAsync("global.json", """{ "sdk": { "version": "99.99.999" } }""");
        var project = await temporary.WriteAsync("App/App.csproj", Project());
        await temporary.WriteAsync("App/App.cs", "public class App { }");

        var error = await Assert.ThrowsAsync<MsBuildLoadException>(() => new MsBuildSolutionLoader().LoadAsync(project));

        Assert.NotNull(error.InnerException);
        Assert.NotNull(error.Diagnostics);
    }

    /// <summary>The same failure through the tool: index_solution answers with the failure rather than
    /// letting it out, and the diagnostics it had are paged like any others.</summary>
    [Fact]
    public async Task A_load_that_throws_reaches_index_solution_as_a_failed_result()
    {
        using var temporary = new TemporaryTree();
        await temporary.WriteAsync("global.json", """{ "sdk": { "version": "99.99.999" } }""");
        await temporary.WriteAsync("App/App.csproj", Project());
        await temporary.WriteAsync("App/App.cs", "public class App { }");
        var session = new WorkspaceSession();
        await session.InitializeAsync(temporary.Path, ["App/App.csproj"], null);

        var result = await session.IndexSolutionAsync("App/App.csproj", null);

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.NotNull(result.Diagnostics);
    }

    [Fact]
    public async Task An_exception_carries_the_diagnostics_gathered_before_it()
    {
        using var temporary = new TemporaryTree();
        await temporary.WriteAsync("Good/Good.csproj", Project());
        var solution = await temporary.WriteAsync("Missing.slnx",
            "<Solution><Project Path=\"Good/Good.csproj\" /><Project Path=\"Gone/Gone.csproj\" /></Solution>");
        WorkspaceDiagnostic[] collected;
        using (var loaded = await new MsBuildSolutionLoader().LoadAsync(solution))
        {
            collected = loaded.Diagnostics.ToArray();
        }
        Assert.NotEmpty(collected);

        var thrown = new MsBuildLoadException(new InvalidOperationException("the load stopped here"), collected);

        Assert.Equal("the load stopped here", thrown.Message);
        Assert.Equal(collected.Length, thrown.Diagnostics.Count);
        Assert.Contains(thrown.Diagnostics, diagnostic => diagnostic.Message.Contains("Gone.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_analyzer_that_is_not_on_disk_is_dropped_with_a_diagnostic_naming_it()
    {
        using var temporary = new TemporaryTree();
        var (loaded, absent, _) = await LoadWithAnalyzersAsync(temporary);
        using (loaded)
        {
            Assert.DoesNotContain(loaded.Solution.Projects.Single().AnalyzerReferences,
                                  reference => string.Equals(reference.FullPath, absent, StringComparison.OrdinalIgnoreCase));
            var diagnostic = Assert.Single(loaded.Diagnostics,
                candidate => candidate.Message.Contains(absent, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(WorkspaceDiagnosticKind.Warning, diagnostic.Kind);
            Assert.Contains("Analyzed", diagnostic.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_analyzer_that_exists_is_kept()
    {
        using var temporary = new TemporaryTree();
        var (loaded, _, present) = await LoadWithAnalyzersAsync(temporary);
        using (loaded)
        {
            Assert.Contains(loaded.Solution.Projects.Single().AnalyzerReferences,
                            reference => string.Equals(reference.FullPath, present, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(loaded.Diagnostics, candidate => candidate.Message.Contains(present, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The project is deleted between the two calls: a second page that re-indexed would fail on
    /// the missing file, and the timestamp of the first index would not survive it.</summary>
    [Fact]
    public async Task The_second_page_arrives_without_indexing_again()
    {
        using var temporary = new TemporaryTree();
        var projectPath = await temporary.WriteAsync("App.csproj", Project());
        await temporary.WriteAsync("Controller.cs", "public sealed class Controller { public void Get() { } }");
        var session = new WorkspaceSession();
        await session.InitializeAsync(temporary.Path, ["App.csproj"], null);

        var first = await session.IndexSolutionAsync("App.csproj", new PageArguments { Page = 1, PageSize = 1 });
        Assert.True(first.Succeeded);
        File.Delete(projectPath);

        var second = await session.IndexSolutionAsync("App.csproj", new PageArguments { Page = 2, PageSize = 1 });

        Assert.True(second.Succeeded);
        Assert.Equal(first.IndexedAt, second.IndexedAt);
        Assert.Equal(2, second.Diagnostics.Page);
    }

    [Fact]
    public void A_message_longer_than_the_response_limit_is_reassembled_from_its_fragments()
    {
        var message = string.Concat(Enumerable.Range(0, 2000).Select(index => $"line-{index:D5};"));
        Assert.True(Encoding.UTF8.GetByteCount(message) > ResponseEnvelope.MaximumSerializedBytes);
        var described = WorkspaceSession.Describe([new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, message)]);

        var fragments = new List<WorkspaceDiagnosticResult>();
        for (var page = 1; ; page++)
        {
            var envelope = WorkspaceSession.PageDiagnostics(described, new PageArguments { Page = page, PageSize = 50 });
            Assert.NotEmpty(envelope.Items);
            fragments.AddRange(envelope.Items);
            if (page >= envelope.Pages)
                break;
        }

        Assert.Equal(described.Count, fragments.Count);
        Assert.Single(fragments.Select(fragment => fragment.Id).Distinct(StringComparer.Ordinal));
        Assert.Equal(Enumerable.Range(1, fragments.Count), fragments.Select(fragment => fragment.Part));
        Assert.Equal(message, string.Concat(fragments.OrderBy(fragment => fragment.Part).Select(fragment => fragment.Message)));
    }

    [Fact]
    public async Task LoadComplete_is_true_when_every_declared_project_arrived()
    {
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(FixtureSolution);

        Assert.True(loaded.Coverage.LoadComplete);
        Assert.Equal(2, loaded.Coverage.ProjectsExpected);
        Assert.Equal(2, loaded.Coverage.ProjectsLoaded);
        Assert.Empty(loaded.Coverage.MissingProjects);
    }

    [Fact]
    public async Task LoadComplete_is_false_and_missingProjects_names_the_one_that_dropped()
    {
        using var temporary = new TemporaryTree();
        await temporary.WriteAsync("Good/Good.csproj", Project());
        var solution = await temporary.WriteAsync("Missing.slnx",
            "<Solution><Project Path=\"Good/Good.csproj\" /><Project Path=\"Gone/Gone.csproj\" /></Solution>");

        using var loaded = await new MsBuildSolutionLoader().LoadAsync(solution);

        Assert.False(loaded.Coverage.LoadComplete);
        Assert.Equal(["Gone.csproj"], loaded.Coverage.MissingProjects);
    }

    /// <summary>The count that would have said the load was whole. One declared project fell out and one
    /// project came in because something referenced it, so the two numbers match and the two sets do not.</summary>
    [Fact]
    public async Task Equal_counts_with_a_different_composition_still_report_an_incomplete_load()
    {
        using var temporary = new TemporaryTree();
        await temporary.WriteAsync("Other/Other.csproj", Project());
        await temporary.WriteAsync("Good/Good.csproj", Project("""<ProjectReference Include="..\Other\Other.csproj" />"""));
        var solution = await temporary.WriteAsync("Swapped.slnx",
            "<Solution><Project Path=\"Good/Good.csproj\" /><Project Path=\"Gone/Gone.csproj\" /></Solution>");

        using var loaded = await new MsBuildSolutionLoader().LoadAsync(solution);

        Assert.Equal(loaded.Coverage.ProjectsExpected, loaded.Coverage.ProjectsLoaded);
        Assert.False(loaded.Coverage.LoadComplete);
        Assert.Equal(["Gone.csproj"], loaded.Coverage.MissingProjects);
    }

    /// <summary>A project entry point declares itself and nothing else: what it references is opened as a
    /// consequence and is neither owed nor spare.</summary>
    [Fact]
    public async Task A_project_entry_point_is_complete_with_its_single_expected_project()
    {
        using var temporary = new TemporaryTree();
        await temporary.WriteAsync("Other/Other.csproj", Project());
        var projectPath = await temporary.WriteAsync("Good/Good.csproj", Project("""<ProjectReference Include="..\Other\Other.csproj" />"""));

        using var loaded = await new MsBuildSolutionLoader().LoadAsync(projectPath);

        Assert.True(loaded.Coverage.LoadComplete);
        Assert.Equal(1, loaded.Coverage.ProjectsExpected);
        Assert.Equal(2, loaded.Coverage.ProjectsLoaded);
    }

    private static string FixtureSolution => FindRepositoryFile("plugins", "cache-detective", "src",
        "CacheDetective.Cli.Tests", "Fixtures", "WarningsAsErrors", "WarningsAsErrors.slnx");

    /// <summary>The same load without the global properties, which is what the loader used to do.</summary>
    private static async Task<(string[] Projects, WorkspaceDiagnostic[] Diagnostics)> OpenWithoutPropertiesAsync()
    {
        MsBuildSolutionLoader.EnsureMsBuildRegistered();
        using var workspace = MSBuildWorkspace.Create();
        var diagnostics = new List<WorkspaceDiagnostic>();
#pragma warning disable CS0618
        workspace.WorkspaceFailed += (_, eventArgs) => diagnostics.Add(eventArgs.Diagnostic);
#pragma warning restore CS0618
        var solution = await workspace.OpenSolutionAsync(FixtureSolution);
        return (solution.Projects.Select(project => project.Name).Order(StringComparer.Ordinal).ToArray(), diagnostics.ToArray());
    }

    private static async Task<(MsBuildLoadResult Loaded, string Absent, string Present)> LoadWithAnalyzersAsync(TemporaryTree temporary)
    {
        var present = await temporary.WriteAsync("Present.dll", "not really an assembly, and never loaded");
        var absent = Path.Combine(temporary.Path, "Absent.dll");
        var projectPath = await temporary.WriteAsync("Analyzed.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableNETAnalyzers>false</EnableNETAnalyzers>
              </PropertyGroup>
              <ItemGroup>
                <Analyzer Include="{present}" />
                <Analyzer Include="{absent}" />
              </ItemGroup>
            </Project>
            """);

        return (await new MsBuildSolutionLoader().LoadAsync(projectPath), absent, present);
    }

    private static string Project(string items = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            {items}
          </ItemGroup>
        </Project>
        """;

    private static string FindRepositoryFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(relativePath)}.");
    }

    private sealed class TemporaryTree : IDisposable
    {
        public TemporaryTree()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cache-detective-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public async Task<string> WriteAsync(string relativePath, string content)
        {
            var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
            return full;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
