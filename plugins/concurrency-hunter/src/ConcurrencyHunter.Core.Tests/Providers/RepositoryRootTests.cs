using ConcurrencyHunter.Providers.LibraryModels;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class RepositoryRootTests
{
    [Fact]
    public void Nearest_folder_holding_the_directory_wins()
    {
        using var tree = new Tree();
        tree.Folder(".concurrency-hunter");
        tree.Folder("repo", ".concurrency-hunter");
        var solution = tree.Folder("repo", "src", "app");

        Assert.Equal(Path.Combine(tree.Root, "repo"), RepositoryRoot.Find(solution));
    }

    [Fact]
    public void Solution_folder_itself_counts()
    {
        using var tree = new Tree();
        var solution = tree.Folder("repo", "src");
        tree.Folder("repo", "src", ".concurrency-hunter");

        Assert.Equal(solution, RepositoryRoot.Find(solution));
    }

    [Fact]
    public void Git_work_tree_root_when_no_folder_holds_one()
    {
        using var tree = new Tree();
        tree.Folder("repo", ".git");
        var solution = tree.Folder("repo", "src");

        Assert.Equal(Path.Combine(tree.Root, "repo"), RepositoryRoot.Find(solution));
    }

    [Fact]
    public void Git_file_marks_a_work_tree_root()
    {
        using var tree = new Tree();
        var repository = tree.Folder("repo");
        File.WriteAllText(Path.Combine(repository, ".git"), "gitdir: elsewhere");
        var solution = tree.Folder("repo", "src");

        Assert.Equal(repository, RepositoryRoot.Find(solution));
    }

    [Fact]
    public void Solution_folder_when_outside_git()
    {
        using var tree = new Tree();
        var solution = tree.Folder("repo", "src");

        Assert.Equal(solution, RepositoryRoot.Find(solution));
    }

    [Fact]
    public void Missing_folder_walks_up_without_failing()
    {
        using var tree = new Tree();
        var repository = tree.Folder("repo");
        tree.Folder("repo", ".concurrency-hunter");

        Assert.Equal(repository, RepositoryRoot.Find(Path.Combine(repository, "missing", "solution")));
    }

    private sealed class Tree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ch-root-").FullName;

        public string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([Root, .. parts])).FullName;

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
