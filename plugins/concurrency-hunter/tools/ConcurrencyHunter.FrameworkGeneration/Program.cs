// Writes the shared frameworks' census into the built-in models from the census runs, and compares its record with the previous one;
// build/generate-frameworks.ps1 drives it (docs/adr/0022).
//   write --built-in <dir> --record <tsv> --commit <sha> --engine <fingerprint> --runs <folder>[;<folder>...] [--previous <tsv>] [--examples 5]
// The runs are the scratch folders of `metrics --census`, in the order they ran. The report compares with --previous, by default the
// record as it was before this write; a regeneration that writes several times passes the record it started from.
using ConcurrencyHunter.Providers.LibraryModels.Generation;

return args switch
{
    ["write", .. var rest] when Options(rest) is { } options && Required(options) => Write(options),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("usage: write --built-in <dir> --record <tsv> --commit <sha> --engine <fingerprint> --runs <folder>[;<folder>...] " +
                            "[--previous <tsv>] [--examples 5]");
    return 2;
}

static Dictionary<string, string>? Options(string[] rest)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < rest.Length; index += 2)
    {
        if (!rest[index].StartsWith("--", StringComparison.Ordinal) || index + 1 == rest.Length || !options.TryAdd(rest[index][2..], rest[index + 1]))
            return null;
    }
    return options;
}

static bool Required(IReadOnlyDictionary<string, string> options) =>
    new[] { "built-in", "record", "commit", "engine", "runs" }.All(options.ContainsKey);

static int Write(IReadOnlyDictionary<string, string> options)
{
    var frameworks = FrameworkAssemblies.Installed(ImplementationAssemblies.ForThisProcess().DotnetRoot);
    var runs = options["runs"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var record = options["record"];
    var before = options.GetValueOrDefault("previous", record);
    var previous = File.Exists(before) ? CensusRecord.Parse(File.ReadLines(before)) : null;

    var harvest = FrameworkCensus.Harvest(runs, frameworks);
    var written = FrameworkCensus.Plan(harvest.Answers, options["built-in"], frameworks, options["commit"], options["engine"]);
    // Core plans the edits and this tool makes them, so that nothing in the executable writes the built-in files.
    foreach (var edit in written.Edits)
    {
        if (edit.Bytes is null)
        {
            File.Delete(edit.Path);
            continue;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(edit.Path)!);
        File.WriteAllBytes(edit.Path, edit.Bytes);
    }
    File.WriteAllLines(record, written.Record.Lines);

    foreach (var problem in harvest.Problems.Concat(written.Problems))
        Console.WriteLine($"problem: {problem}");
    foreach (var line in FrameworkCensus.Compare(previous, written.Record, int.Parse(options.GetValueOrDefault("examples", "5"))))
        Console.WriteLine(line);
    var ships = written.Record.Rows.GroupBy(row => row.Ships, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                       .Select(group => $"{group.Key} {group.Count()}");
    Console.WriteLine($"census: {written.Record.Rows.Count} members ({string.Join(", ", ships)}); framework files {written.Bytes} bytes; record {record}");
    return 0;
}
