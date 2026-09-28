namespace ConcurrencyHunter.Providers.LibraryModels;

public static class RepositoryRoot
{
    public static string Find(string solutionDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(solutionDirectory));
        string? gitRoot = null;
        for (var current = directory; current is not null; current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".concurrency-hunter")))
                return current.FullName;
            if (gitRoot is null && (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                                    File.Exists(Path.Combine(current.FullName, ".git"))))
                gitRoot = current.FullName;
        }

        return gitRoot ?? directory.FullName;
    }
}
