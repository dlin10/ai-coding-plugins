using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Scopes;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class ProcessScopeTests
{
    private const string ROOT_DIRECTORY = @"C:\fixture";
    private const string PROGRAM = "System.Console.WriteLine();";
    private const string LIBRARY = "public static class Shared { public static string? Value; }";

    [Fact]
    public void Executable_is_a_scope_of_itself_and_its_transitive_project_references()
    {
        var discovery = Discover(
            new FixtureOptions
            {
                ProjectOutputKinds = Executables("Web"),
                ProjectReferences = [("Web", "Application"), ("Application", "Domain")]
            },
            ("Web", "Program.cs", PROGRAM),
            ("Application", "Service.cs", "public sealed class Service { }"),
            ("Domain", "Shared.cs", LIBRARY),
            ("Unrelated", "Other.cs", "public sealed class Other { }"));

        var scope = Assert.Single(discovery.Scopes).Scope;
        Assert.Equal("Web", scope.Id);
        Assert.Equal("Web", scope.ExecutableProject);
        Assert.Equal(["Application", "Domain", "Web"], scope.Projects);
        Assert.Empty(discovery.Diagnostics);
    }

    [Fact]
    public void Library_in_two_executables_belongs_to_both_scopes_ordered_by_id()
    {
        var discovery = Discover(
            new FixtureOptions
            {
                ProjectOutputKinds = Executables("Worker", "Api"),
                ProjectReferences = [("Worker", "Domain"), ("Api", "Domain")]
            },
            ("Worker", "Program.cs", PROGRAM),
            ("Api", "Program.cs", PROGRAM),
            ("Domain", "Shared.cs", LIBRARY));

        Assert.Equal(["Api", "Worker"], discovery.Scopes.Select(scoped => scoped.Scope.Id));
        Assert.All(discovery.Scopes, scoped => Assert.Contains("Domain", scoped.Scope.Projects));
        Assert.All(discovery.Scopes, scoped => Assert.Equal(scoped.Scope.Projects, scoped.Projects.Select(project => project.Name)));
    }

    [Fact]
    public void Executables_sharing_an_assembly_name_get_ids_with_their_project_paths()
    {
        var discovery = Discover(
            new FixtureOptions
            {
                ProjectOutputKinds = Executables("First", "Second", "Third"),
                ProjectAssemblyNames = new Dictionary<string, string> { ["First"] = "App", ["Second"] = "App" }
            },
            ("First", "Program.cs", PROGRAM),
            ("Second", "Program.cs", PROGRAM),
            ("Third", "Program.cs", PROGRAM));

        Assert.Equal(["App@First/First.csproj", "App@Second/Second.csproj", "Third"],
                     discovery.Scopes.Select(scoped => scoped.Scope.Id));
    }

    [Fact]
    public void Windows_and_windows_runtime_applications_are_executables()
    {
        var discovery = Discover(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind>
                {
                    ["Desktop"] = OutputKind.WindowsApplication,
                    ["Store"] = OutputKind.WindowsRuntimeApplication
                }
            },
            ("Desktop", "Program.cs", PROGRAM),
            ("Store", "Program.cs", PROGRAM),
            ("Library", "Shared.cs", LIBRARY));

        Assert.Equal(["Desktop", "Store"], discovery.Scopes.Select(scoped => scoped.Scope.Id));
    }

    [Theory]
    [InlineData("Microsoft.VisualStudio.TestPlatform.ObjectModel")]
    [InlineData("Microsoft.Testing.Platform")]
    [InlineData("xunit.core")]
    [InlineData("xunit.v3.core")]
    [InlineData("nunit.framework")]
    [InlineData("Microsoft.VisualStudio.TestPlatform.TestFramework")]
    public void Executable_test_project_is_not_a_scope(string testFramework)
    {
        var discovery = Discover(
            new FixtureOptions
            {
                ProjectOutputKinds = Executables("App", "App.Tests"),
                ProjectReferences = [("App.Tests", "App")],
                ProjectExtraAssemblyNames = new Dictionary<string, IReadOnlyList<string>> { ["App.Tests"] = [testFramework] }
            },
            ("App", "Program.cs", PROGRAM),
            ("App.Tests", "Program.cs", PROGRAM));

        var scope = Assert.Single(discovery.Scopes).Scope;
        Assert.Equal("App", scope.Id);
        Assert.Equal(["App"], scope.Projects);
    }

    [Fact]
    public void Solution_without_an_executable_is_one_solution_scope_with_a_diagnostic()
    {
        var discovery = Discover(
            new FixtureOptions { ProjectReferences = [("Application", "Domain")] },
            ("Application", "Service.cs", "public sealed class Service { }"),
            ("Domain", "Shared.cs", LIBRARY));

        var scope = Assert.Single(discovery.Scopes).Scope;
        Assert.Equal("solution", scope.Id);
        Assert.Null(scope.ExecutableProject);
        Assert.Equal(["Application", "Domain"], scope.Projects);
        Assert.Equal(["No executable project was found; the whole solution is analyzed as one process scope."], discovery.Diagnostics);
    }

    [Fact]
    public void Solution_whose_only_executables_are_test_projects_is_one_solution_scope()
    {
        var discovery = Discover(
            new FixtureOptions
            {
                ProjectOutputKinds = Executables("Library.Tests"),
                ProjectReferences = [("Library.Tests", "Library")],
                ProjectExtraAssemblyNames = new Dictionary<string, IReadOnlyList<string>> { ["Library.Tests"] = ["xunit.v3.core"] }
            },
            ("Library", "Shared.cs", LIBRARY),
            ("Library.Tests", "Program.cs", PROGRAM));

        var scope = Assert.Single(discovery.Scopes).Scope;
        Assert.Equal(ProcessScope.SOLUTION_SCOPE_ID, scope.Id);
        Assert.Equal(["Library", "Library.Tests"], scope.Projects);
        Assert.Equal([ProcessScope.NO_EXECUTABLE_DIAGNOSTIC], discovery.Diagnostics);
    }

    [Fact]
    public void Library_referencing_a_test_framework_is_not_an_executable_and_is_not_a_test_scope()
    {
        var discovery = Discover(
            new FixtureOptions
            {
                ProjectOutputKinds = Executables("App"),
                ProjectReferences = [("App", "Helpers")],
                ProjectExtraAssemblyNames = new Dictionary<string, IReadOnlyList<string>> { ["Helpers"] = ["nunit.framework"] }
            },
            ("App", "Program.cs", PROGRAM),
            ("Helpers", "Shared.cs", LIBRARY));

        Assert.Equal(["App", "Helpers"], Assert.Single(discovery.Scopes).Scope.Projects);
    }

    private static ProcessScopeDiscovery Discover(FixtureOptions options, params (string Project, string Path, string Source)[] files) =>
        ProcessScopes.Discover(FixtureSolution.CreateProjects(options, files), ROOT_DIRECTORY);

    private static Dictionary<string, OutputKind> Executables(params string[] projects) =>
        projects.ToDictionary(project => project, _ => OutputKind.ConsoleApplication);
}
