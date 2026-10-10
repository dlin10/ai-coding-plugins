// Generates every public member of the installed System.Private.CoreLib and compares generation snapshots; build/generate-corelib.ps1
// drives it.
//   generate --out <dir> [--major 8] [--parallelism N] [--members <file>] [--timeout-minutes 5]
//   compare <before.tsv> <after.tsv> [--examples 5] [--members <file>]
// generate resumes: a member already in <out>/results.tsv is not generated again.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using ConcurrencyHunter.Providers.LibraryModels.Generation;

const string CORELIB = "System.Private.CoreLib";

return args switch
{
    ["generate", .. var rest] when Options(rest) is { } options && options.ContainsKey("out") => Generate(options),
    ["compare", var before, var after, .. var rest] when Options(rest) is { } options => Compare(before, after, options),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("usage: generate --out <dir> [--major 8] [--parallelism N] [--members <file>] [--timeout-minutes 5]");
    Console.Error.WriteLine("       compare <before.tsv> <after.tsv> [--examples 5] [--members <file>]");
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

static int Generate(IReadOnlyDictionary<string, string> options)
{
    var outDir = options["out"];
    var major = int.Parse(options.GetValueOrDefault("major", "8"));
    var parallelism = int.Parse(options.GetValueOrDefault("parallelism", Math.Max(1, Environment.ProcessorCount / 2).ToString()));
    var limit = TimeSpan.FromMinutes(double.Parse(options.GetValueOrDefault("timeout-minutes", "5"), CultureInfo.InvariantCulture));
    Directory.CreateDirectory(outDir);
    var clock = Stopwatch.StartNew();
    void Log(string text) => Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {text}");

    var assembly = InstalledCoreLib(major);
    var library = LibraryCompilation.Compile(assembly, CancellationToken.None);
    if (library.Compilation is not { } compilation)
    {
        Console.Error.WriteLine($"{CORELIB} does not compile: {library.Reason}: {string.Join(", ", library.Errors)}");
        return 1;
    }

    var implementation = $"{CORELIB} {assembly.AssemblyVersion} file {assembly.FileVersion} mvid {assembly.Mvid}";
    IReadOnlyList<string> members = options.TryGetValue("members", out var list)
        ? File.ReadLines(list).Where(line => line.Length != 0).Distinct(StringComparer.Ordinal).ToArray()
        : CoreLibGeneration.PublicMembers(compilation);
    var resultsPath = Path.Combine(outDir, "results.tsv");
    var done = File.Exists(resultsPath)
        ? File.ReadLines(resultsPath).Select(line => line.Split('\t', 2)[0]).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);
    var pending = members.Where(member => !done.Contains(member)).ToArray();
    Log($"compiled {implementation}: {library.Bodies} bodies; {members.Count} members, {done.Count} done before, {pending.Length} to run, " +
        $"parallelism {parallelism}");

    // Each answer is written as it comes, so a run cut short resumes where it stopped; the answer document keeps the words of every
    // cause and the timings, which the snapshot leaves out.
    using (var results = TextWriter.Synchronized(new StreamWriter(resultsPath, append: true) { AutoFlush = true }))
    {
        using (var answers = TextWriter.Synchronized(new StreamWriter(Path.Combine(outDir, "answers.jsonl"), append: true) { AutoFlush = true }))
        {
            using (var failures = TextWriter.Synchronized(new StreamWriter(Path.Combine(outDir, "failures.txt"), append: true) { AutoFlush = true }))
            {
                var finished = 0;
                Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, member =>
                {
                    GenerationSnapshotRow row;
                    using var timeout = new CancellationTokenSource(limit);
                    try
                    {
                        var answer = ModelGenerator.Trace(new GenerationRequest(CORELIB, assembly.AssemblyVersion, member, null, null), library, timeout.Token)
                                                   .Answer;
                        answers.Write(Encoding.UTF8.GetString(GeneratedDocument.Write(answer)));
                        row = GenerationSnapshotRow.Of(answer);
                    }
                    catch (OperationCanceledException)
                    {
                        row = new GenerationSnapshotRow(member, "timeout", [], "");
                    }
                    catch (Exception error)
                    {
                        failures.WriteLine($"{member}\t{error.GetType().Name}: {error.Message.ReplaceLineEndings(" ")}");
                        row = new GenerationSnapshotRow(member, "threw", [], "");
                    }

                    results.WriteLine(row.Line);
                    if (Interlocked.Increment(ref finished) % 100 == 0)
                        Log($"  {finished}/{pending.Length}");
                });
            }
        }
    }

    var wanted = members.ToHashSet(StringComparer.Ordinal);
    var rows = File.ReadLines(resultsPath).Select(GenerationSnapshotRow.Parse).Where(row => wanted.Contains(row.Member))
                   .DistinctBy(row => row.Member, StringComparer.Ordinal).ToArray();
    var snapshot = new GenerationSnapshot(implementation, rows);
    File.WriteAllLines(Path.Combine(outDir, "snapshot.tsv"), snapshot.Lines);
    foreach (var group in rows.GroupBy(row => row.Outcome, StringComparer.Ordinal).OrderByDescending(group => group.Count()))
        Log($"{group.Key,-26} {group.Count(),6}");
    Log($"done: {rows.Length} members in {Path.Combine(outDir, "snapshot.tsv")}");
    return 0;
}

static int Compare(string beforePath, string afterPath, IReadOnlyDictionary<string, string> options)
{
    var before = GenerationSnapshot.Parse(File.ReadLines(beforePath));
    var after = GenerationSnapshot.Parse(File.ReadLines(afterPath));
    // A run over some members compares only their rows: the others are not missing, they were not asked for.
    if (options.TryGetValue("members", out var list))
    {
        var members = File.ReadLines(list).Where(line => line.Length != 0).ToHashSet(StringComparer.Ordinal);
        before = before with { Rows = before.Rows.Where(row => members.Contains(row.Member)).ToArray() };
        after = after with { Rows = after.Rows.Where(row => members.Contains(row.Member)).ToArray() };
    }
    var report = CoreLibGeneration.Compare(before, after, int.Parse(options.GetValueOrDefault("examples", "5")));
    foreach (var line in report)
        Console.WriteLine(line);
    Console.WriteLine(report.Count == 0 ? "The snapshots are the same." : $"{report.Count} difference line(s).");
    return report.Count == 0 ? 0 : 1;
}

// The newest installed shared-framework System.Private.CoreLib of a major version, opened as the generator opens an implementation.
static ImplementationAssembly InstalledCoreLib(int major)
{
    var shared = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), ".."));
    var directory = Directory.GetDirectories(shared)
                             .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
                             .Where(item => item.Version is { } version && version.Major == major && version.Minor == 0)
                             .OrderByDescending(item => item.Version).Select(item => item.Path).FirstOrDefault()
                    ?? throw new InvalidOperationException($"No shared framework {major}.0 is installed under {shared}.");
    var dll = Path.Combine(directory, CORELIB + ".dll");
    using var stream = File.OpenRead(dll);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    return new ImplementationAssembly(CORELIB, dll, null, Path.GetFileName(directory), $"net{major}.0", directory, [], [],
                                      AssemblyName.GetAssemblyName(dll).Version!.ToString(), FileVersionInfo.GetVersionInfo(dll).FileVersion!,
                                      metadata.GetGuid(metadata.GetModuleDefinition().Mvid));
}
