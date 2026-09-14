using ConcurrencyHunter.Runs;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

public sealed class TargetResolverTests
{
    [Fact]
    public void Sln_file_resolves_to_itself()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("Demo.sln");

        var result = TargetResolver.Resolve(path);

        Assert.Equal(TargetKind.Resolved, result.Kind);
        Assert.Equal(Path.GetFullPath(path), result.Path);
    }

    [Fact]
    public void Slnx_and_csproj_files_resolve_to_themselves()
    {
        using var directory = new TemporaryDirectory();
        var slnx = directory.File("Demo.slnx");
        var csproj = directory.File("Demo.csproj");

        Assert.Equal(Path.GetFullPath(slnx), TargetResolver.Resolve(slnx).Path);
        Assert.Equal(Path.GetFullPath(csproj), TargetResolver.Resolve(csproj).Path);
    }

    [Fact]
    public void Directory_with_one_top_level_solution_resolves_to_it()
    {
        using var directory = new TemporaryDirectory();
        var solution = directory.File("Demo.slnx");

        var result = TargetResolver.Resolve(directory.Path);

        Assert.Equal(TargetKind.Resolved, result.Kind);
        Assert.Equal(Path.GetFullPath(solution), result.Path);
    }

    [Fact]
    public void Directory_with_several_top_level_solutions_returns_sorted_candidates()
    {
        using var directory = new TemporaryDirectory();
        directory.File("z.sln");
        directory.File("a.slnx");

        var result = TargetResolver.Resolve(directory.Path);

        Assert.Equal(TargetKind.Ambiguous, result.Kind);
        Assert.Equal(["a.slnx", "z.sln"], result.Candidates);
    }

    [Fact]
    public void Solution_below_the_top_level_is_not_considered()
    {
        using var directory = new TemporaryDirectory();
        var nested = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "nested")).FullName;
        File.WriteAllText(System.IO.Path.Combine(nested, "Demo.slnx"), "<Solution />");

        var result = TargetResolver.Resolve(directory.Path);

        Assert.Equal(TargetKind.Unresolvable, result.Kind);
    }

    [Fact]
    public void Directory_without_a_solution_is_unresolvable()
    {
        using var directory = new TemporaryDirectory();

        var result = TargetResolver.Resolve(directory.Path);

        Assert.Equal(TargetKind.Unresolvable, result.Kind);
        Assert.Contains(directory.Path, result.Reason);
    }

    [Fact]
    public void Missing_path_and_other_extensions_are_unresolvable()
    {
        using var directory = new TemporaryDirectory();
        var missing = System.IO.Path.Combine(directory.Path, "missing.slnx");
        var other = directory.File("notes.txt");

        var missingResult = TargetResolver.Resolve(missing);
        var otherResult = TargetResolver.Resolve(other);

        Assert.Equal(TargetKind.Unresolvable, missingResult.Kind);
        Assert.Contains("missing.slnx", missingResult.Reason);
        Assert.Equal(TargetKind.Unresolvable, otherResult.Kind);
        Assert.Contains("notes.txt", otherResult.Reason);
    }

    [Fact]
    public void Relative_or_blank_target_is_unresolvable()
    {
        Assert.Equal(TargetKind.Unresolvable, TargetResolver.Resolve("Demo.slnx").Kind);
        Assert.Equal(TargetKind.Unresolvable, TargetResolver.Resolve(" ").Kind);
        Assert.Equal(TargetKind.Unresolvable, TargetResolver.Resolve(null).Kind);
        Assert.Equal("target must be an absolute path", TargetResolver.Resolve(null).Reason);
    }

    [Fact]
    public void Directory_that_cannot_be_enumerated_is_unresolvable_with_the_exception()
    {
        using var directory = new TemporaryDirectory();

        var result = TargetResolver.Resolve(
            directory.Path,
            _ => throw new UnauthorizedAccessException("denied"));

        Assert.Equal(TargetKind.Unresolvable, result.Kind);
        Assert.Equal("UnauthorizedAccessException: denied", result.Reason);
    }

    [Fact]
    public void Target_over_1024_bytes_is_too_long()
    {
        var target = @"C:\" + new string('я', 200);

        var result = TargetResolver.Resolve(target);

        Assert.Equal(TargetKind.TooLong, result.Kind);
    }

    [Fact]
    public void Resolved_solution_path_over_1024_bytes_is_too_long()
    {
        using var root = new TemporaryDirectory();
        var length = 100;
        string directory;
        string solution;
        do
        {
            directory = System.IO.Path.Combine(root.Path, new string('я', length++));
            solution = System.IO.Path.Combine(directory, new string('ю', 20) + ".slnx");
        }
        while (ResponseBudget.Weight(directory) <= 900 || ResponseBudget.Weight(solution) <= 1024);
        Directory.CreateDirectory(directory);
        File.WriteAllText(solution, "<Solution />");

        var result = TargetResolver.Resolve(directory);

        Assert.True(ResponseBudget.Weight(directory) <= 1024);
        Assert.Equal(TargetKind.TooLong, result.Kind);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"concurrency-hunter-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string File(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(path, name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                ? "<Solution />"
                : string.Empty);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
