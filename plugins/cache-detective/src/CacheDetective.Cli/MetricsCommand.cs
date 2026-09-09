using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using CacheDetective.Cli;
using CacheDetective.Configuration;
using CacheDetective.Serialization;
using CacheDetective.Caching;
using CacheDetective.Events;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Mcp;
using CacheDetective.Workspaces;

namespace CacheDetective;

internal static class MetricsCommand
{
    private const string MANIFEST = "skills/scan/evals/metrics/manifest.json";

    /// <summary>Every edge kind, so that one which fell to zero is a zero in the file rather than a
    /// missing key: a reader comparing two rows must be able to tell an empty kind from a row written
    /// before the kind was reported. The names are the ones <see cref="Mcp.TraceQueries.EdgeType"/>
    /// produces, so a measurement and an <c>export_graph</c> dump speak one vocabulary.</summary>
    private static readonly string[] EDGE_KINDS =
        ["caches", "calls", "consumes", "fires", "invalidates", "publishes", "reads", "serves", "writes"];

    /// <summary>What a revision reads as when git could not produce one. It is not a revision and nothing
    /// may be pinned to it.</summary>
    internal const string UnknownRevision = "unknown";

    /// <summary>How a solution is loaded. A parameter rather than shared state, because the test suite
    /// runs its classes in parallel and a process-wide counter would measure another test's loads.</summary>
    internal delegate Task<MsBuildLoadResult> SolutionLoad(string path, CancellationToken cancellationToken);

    internal static async Task<int> RunAsync(string[] args, SolutionLoad? load = null)
    {
        try
        {
            var options = Options.Parse(args);
            options.Load = load ?? ((path, token) => new MsBuildSolutionLoader().LoadAsync(path, token));
            return options.Mode switch
            {
                MetricsMode.Measure => await MeasureAsync(options).ConfigureAwait(false),
                MetricsMode.Sample => await SampleAsync(options).ConfigureAwait(false),
                MetricsMode.Compare => await CompareAsync(options).ConfigureAwait(false),
                MetricsMode.Score => await ScoreAsync(options).ConfigureAwait(false),
                _ => ExitCode.UsageError
            };
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return ExitCode.UsageError;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return ExitCode.Failure;
        }
    }

    private static async Task<int> MeasureAsync(Options options)
    {
        var indexed = await IndexAsync(options).ConfigureAwait(false);
        var graph = indexed.Graph;
        var unresolved = Enum.GetValues<UnresolvedKind>().ToDictionary(KindName, _ => 0, StringComparer.Ordinal);
        foreach (var group in graph.Unresolved.GroupBy(item => KindName(item.Kind)))
            unresolved[group.Key] = group.Count();
        var cacheDenominator = graph.CacheOperations.Count + Count(unresolved, "key") + Count(unresolved, "cache_api");
        var sqlDenominator = graph.ParsedSqlSites + Count(unresolved, "sql");
        var cacheCoverage = Coverage(graph.CacheOperations.Count, cacheDenominator);
        var sqlCoverage = Coverage(graph.ParsedSqlSites, sqlDenominator);
        var edgesByKind = EDGE_KINDS.ToDictionary(kind => kind, _ => 0, StringComparer.Ordinal);
        foreach (var group in graph.Edges.GroupBy(TraceQueries.EdgeType))
            edgesByKind[group.Key] = group.Count();
        var result = new Measurement(options.Solution!, indexed.LoadSeconds, indexed.IndexSeconds,
                                     indexed.LoadSeconds + indexed.IndexSeconds, VertexCount(graph), graph.Edges.Count, edgesByKind,
                                     graph.CacheOperations.Count, unresolved, indexed.Diagnostics, indexed.Coverage.ProjectsExpected,
                                     indexed.Coverage.ProjectsLoaded, indexed.Coverage.MissingProjects,
                                     indexed.Coverage.EmptyProjects, indexed.Coverage.LoadComplete,
                                     indexed.Revision.Value, indexed.Revision.WorkingTreeClean, indexed.RecognizersFile,
                                     indexed.RecognizersHash, indexed.RecognizersApplied, indexed.WorkspaceFile,
                                     indexed.WorkspaceHash, indexed.WorkspaceApplied, cacheCoverage, sqlCoverage,
                                     indexed.Revision.WorkingTreeClean,
                                     new MeasurementCounts(VertexCount(graph), graph.Edges.Count, graph.CacheOperations.Count),
                                     new MeasurementCoverage(cacheCoverage, sqlCoverage));
        await WriteAsync(options.Out!, result).ConfigureAwait(false);
        return ExitCode.Ok;
    }

    private static async Task<int> SampleAsync(Options options)
    {
        // A sample is pinned by the revision it was drawn at, so it may only be drawn from a corpus whose
        // revision is knowable and whose tree is clean. Writing one from a modified checkout stamped it
        // with a revision it did not match, and an unknown revision stamped it with the word "unknown"
        // while claiming the tree was clean.
        EnsureComparableCorpus(GetRevision(options.Root!), "--sample");
        var indexed = await IndexAsync(options).ConfigureAwait(false);
        var sample = BuildSample(indexed.Graph, options.Sample!, options.Count!.Value, indexed.Revision.Value, options.Root!);
        var existing = await ReadSampleIfPresentAsync(options.Out!).ConfigureAwait(false);
        if (!options.Force && existing?.Rows.Any(row => !string.IsNullOrWhiteSpace(row.Verdict)) == true)
            throw new ArgumentException($"'{options.Out}' already contains labels; use --force to replace it.");
        await WriteAsync(options.Out!, sample).ConfigureAwait(false);
        return ExitCode.Ok;
    }

    /// <summary>The corpus a sample may be drawn from or checked against: a known revision and nothing
    /// uncommitted. Both halves matter — an unknown revision reports itself as clean, so checking only
    /// cleanliness would let a checkout git cannot read through.</summary>
    private static void EnsureComparableCorpus(RepositoryRevision revision, string mode)
    {
        if (!revision.WorkingTreeClean)
            throw new ArgumentException($"{mode} requires a clean working tree; the corpus has uncommitted changes.");
        if (string.Equals(revision.Value, UnknownRevision, StringComparison.Ordinal))
            throw new ArgumentException($"{mode} requires a corpus revision, and 'git rev-parse HEAD' did not produce one.");
    }

    private static async Task<int> CompareAsync(Options options)
    {
        var stored = await ReadSampleAsync(options.Compare!).ConfigureAwait(false);
        // The kind of sample is what the manifest says the file holds, not what the file says about
        // itself: taking it from the file let one sample's content be checked under another's rules by
        // renaming it.
        var expected = await ReadManifestForFileAsync(options.Compare!, options.Root).ConfigureAwait(false);
        if (!string.Equals(stored.Sample, expected.Sample, StringComparison.Ordinal))
            throw new ArgumentException($"'{options.Compare}' holds a '{stored.Sample}' sample and the manifest expects " +
                                        $"'{expected.Sample}'.");
        var revision = GetRevision(options.Root!);
        EnsureComparableCorpus(revision, "--compare");
        if (!string.Equals(revision.Value, expected.Revision, StringComparison.Ordinal) ||
            !string.Equals(stored.Revision, expected.Revision, StringComparison.Ordinal))
        {
            throw new ArgumentException("--compare requires the corpus revision recorded in the manifest.");
        }

        var indexed = await IndexAsync(options).ConfigureAwait(false);
        var observed = BuildSample(indexed.Graph, expected.Sample, expected.TargetCount, revision.Value, options.Root!);
        var differences = SampleDifferences(stored, observed, expected);
        if (differences.Count == 0)
            return ExitCode.Ok;
        foreach (var difference in differences)
            Console.Error.WriteLine(difference);
        return ExitCode.Failure;
    }

    private static async Task<int> ScoreAsync(Options options)
    {
        var sample = await ReadSampleAsync(options.Score!).ConfigureAwait(false);
        var scores = CalculateScores(sample);
        if (sample.Metrics is not null)
        {
            if (!Equals(sample.Metrics, scores))
            {
                Console.Error.WriteLine("Recorded metrics do not match the labels.");
                return ExitCode.Failure;
            }
            return ExitCode.Ok;
        }

        await WriteAsync(options.Score!, sample with { Metrics = scores }).ConfigureAwait(false);
        return ExitCode.Ok;
    }

    private static async Task<Indexed> IndexAsync(Options options)
    {
        var root = Path.GetFullPath(options.Root!);
        var solutionArgument = options.Solution!;
        var solution = Path.GetFullPath(Path.IsPathRooted(solutionArgument) ? solutionArgument : Path.Combine(root, solutionArgument));
        var recognizers = await ReadRecognizersAsync(options.Recognizers).ConfigureAwait(false);
        var workspace = await ReadWorkspaceAsync(options.Workspace).ConfigureAwait(false);
        var load = Stopwatch.StartNew();
        using var loaded = await options.Load(solution, CancellationToken.None).ConfigureAwait(false);
        load.Stop();
        var index = Stopwatch.StartNew();
        // The same merge a session uses, through the same helper: a declaration measured here and the same
        // declaration indexed in a session must resolve identically, or the measurement is about the
        // command rather than about the tool. See CacheRecognizers.Merge.
        var configured = CacheRecognizers.Merge(recognizers.Concat(workspace.Caches));
        var graph = await new CallGraphIndexer(new IndexerOptions(configured, EventRecognizers.All.Concat(workspace.Events).ToArray()))
            .IndexAsync(loaded.Solution, options.Solution!).ConfigureAwait(false);
        index.Stop();
        return new Indexed(graph, load.Elapsed.TotalSeconds, index.Elapsed.TotalSeconds, loaded.Diagnostics.Count, loaded.Coverage,
                           GetRevision(root), options.Recognizers is null ? null : Path.GetFullPath(options.Recognizers),
                           options.Recognizers is null ? null : HashFile(options.Recognizers!), recognizers.Count,
                           options.Workspace is null ? null : Path.GetFullPath(options.Workspace),
                           options.Workspace is null ? null : HashFile(options.Workspace), workspace.Count);
    }

    /// <summary>
    /// The <c>events</c> and <c>caches</c> a workspace declares, read through the one parser and merged
    /// the way <see cref="Mcp.WorkspaceSession"/> merges them, at <see cref="Confidence.Confirmed"/> —
    /// the repository is stating what its own libraries do.
    /// <para>Without this a corpus whose event bus is declared rather than built in measures as though it
    /// publishes nothing, which is not a fact about the corpus but about the command.</para>
    /// </summary>
    private static async Task<WorkspaceDeclarations> ReadWorkspaceAsync(string? path)
    {
        if (path is null) return new WorkspaceDeclarations([], []);
        WorkspaceConfiguration configuration;
        try { configuration = await WorkspaceConfigurationStore.ReadFileAsync(path).ConfigureAwait(false); }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new ArgumentException($"Invalid workspace file '{path}'. {error.Message}", error);
        }

        return new WorkspaceDeclarations(
            (configuration.Events ?? []).Select(item => item.ToRecognizer(Confidence.Confirmed, null)).ToArray(),
            (configuration.Caches ?? []).Select(item => item.ToRecognizer(Confidence.Confirmed, null)).ToArray());
    }

    /// <summary>The third reader of the one declaration schema. It shares <see cref="CacheRecognizerConfiguration"/>
    /// with the workspace's <c>caches</c> section and with <c>annotate</c>'s <c>cache_api</c>, so a file
    /// written for one is read by all three — which is what lets a "before" run and an "after" run read
    /// the same bytes. The confidence stays <c>likely</c>: a file passed on the command line is a claim
    /// about an API this tool does not know.</summary>
    private static async Task<IReadOnlyList<CacheRecognizer>> ReadRecognizersAsync(string? path)
    {
        if (path is null) return [];
        CacheRecognizerConfiguration[]? declared;
        try { declared = JsonSerializer.Deserialize<CacheRecognizerConfiguration[]>(await File.ReadAllTextAsync(path).ConfigureAwait(false)); }
        catch (JsonException error) { throw new ArgumentException($"Invalid recognizers file '{path}'. {error.Message}", error); }
        if (declared is null)
            throw new ArgumentException("--recognizers must contain a JSON array.");
        try { return declared.Select(configuration => configuration.ToRecognizer(Confidence.Likely, null)).ToArray(); }
        catch (InvalidDataException error) { throw new ArgumentException($"Invalid recognizers file '{path}'. {error.Message}", error); }
    }

    private static MetricsSample BuildSample(CacheGraph graph, string sample, int count, string revision, string root)
    {
        IReadOnlyList<SampleRow> candidates = sample switch
        {
            "role" => RoleCandidates(graph, root),
            "efwrite" => EfWriteCandidates(graph, root),
            _ => throw new ArgumentException("--sample must be role or efwrite.")
        };
        var rows = candidates.OrderBy(row => StableHash(row.Id), StringComparer.Ordinal)
                             .ThenBy(row => row.Id, StringComparer.Ordinal)
                             .Take(count)
                             .ToArray();
        return new MetricsSample(sample, count, candidates.Count, revision, rows);
    }

    private static IReadOnlyList<SampleRow> RoleCandidates(CacheGraph graph, string root) =>
        graph.CacheKeys.Where(key => !string.IsNullOrWhiteSpace(key.Role))
             .Select(key =>
             {
                 var operation = graph.CacheOperations.Where(candidate => candidate.Key.Template == key.Template && candidate.Key.Store == key.Store)
                                             .OrderBy(candidate => candidate.Evidence.FirstOrDefault()?.File, StringComparer.Ordinal)
                                             .ThenBy(candidate => candidate.Evidence.FirstOrDefault()?.Line)
                                             .FirstOrDefault();
                 var evidence = operation?.Evidence.FirstOrDefault();
                 return new SampleRow($"role:{key.Store}:{key.Template}", Relative(root, evidence?.File ?? operation?.Handler.File),
                                      evidence?.Line ?? operation?.Handler.Line ?? 0, key.Role!);
             }).ToArray();

    private static IReadOnlyList<SampleRow> EfWriteCandidates(CacheGraph graph, string root) =>
        graph.HeuristicWriteSites.Select(site =>
             {
                 var file = Relative(root, site.Evidence.File ?? site.Handler.File);
                 return new SampleRow(
                     $"efwrite:{site.Handler.Solution}:{site.Handler.Symbol}:{site.Table.Database}:{site.Table.Name}:{file}:{site.Evidence.Line}",
                     file, site.Evidence.Line ?? site.Handler.Line, "write");
             })
             .GroupBy(row => row.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray();

    /// <summary>
    /// The path a row records, as the corpus sees it. A sample is committed and compared on other
    /// machines, so an absolute checkout path would make both the row and the id that embeds it
    /// machine-specific. Paths outside the corpus root are left alone rather than guessed at.
    /// </summary>
    private static string Relative(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Path.GetFullPath(root), full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? full.Replace('\\', '/')
            : relative.Replace('\\', '/');
    }

    private static SampleScores CalculateScores(MetricsSample sample)
    {
        var labeled = sample.Rows.Where(row => !string.IsNullOrWhiteSpace(row.Verdict) && !string.Equals(row.Verdict, "unclear", StringComparison.Ordinal)).ToArray();
        var unclear = sample.Rows.Count(row => string.Equals(row.Verdict, "unclear", StringComparison.Ordinal));
        if (labeled.Length == 0)
            return new SampleScores(null, sample.Sample == "efwrite" ? null : null, unclear, "not_applicable");
        var accuracy = (double)labeled.Count(Agrees) / labeled.Length;
        double? falsePositiveRate = sample.Sample == "efwrite"
            ? (double)labeled.Count(row => string.Equals(row.Verdict, "false_write", StringComparison.Ordinal)) / labeled.Length
            : null;
        return new SampleScores(accuracy, falsePositiveRate, unclear, "ok");
    }

    /// <summary>
    /// Whether a label confirms the prediction. The role sample answers in the tool's own vocabulary, so
    /// the verdict is the prediction; the efwrite sample answers whether a predicted <c>write</c> is a
    /// real one, so <c>true_write</c> confirms it and comparing the two strings verbatim never could.
    /// </summary>
    private static bool Agrees(SampleRow row) =>
        string.Equals(row.Prediction, row.Verdict, StringComparison.Ordinal) ||
        string.Equals(row.Verdict, "true_write", StringComparison.Ordinal);

    private static CoverageMetric Coverage(int numerator, int denominator) => denominator == 0
        ? new CoverageMetric(null, "not_applicable")
        : new CoverageMetric((double)numerator / denominator, "ok");

    private static async Task<MetricsSample?> ReadSampleIfPresentAsync(string path) => File.Exists(path) ? await ReadSampleAsync(path).ConfigureAwait(false) : null;

    private static async Task<MetricsSample> ReadSampleAsync(string path) =>
        JsonSerializer.Deserialize(await File.ReadAllTextAsync(path).ConfigureAwait(false),
                                   MetricsJsonContext.Default.MetricsSample) ??
        throw new ArgumentException($"'{path}' is not a metrics sample.");

    private static Task WriteAsync(string path, Measurement value) =>
        WriteAsync(path, value, MetricsJsonContext.Default.Measurement);

    private static Task WriteAsync(string path, MetricsSample value) =>
        WriteAsync(path, value, MetricsJsonContext.Default.MetricsSample);

    private static async Task WriteAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, typeInfo) + Environment.NewLine).ConfigureAwait(false);
    }

    private static int VertexCount(CacheGraph graph) => graph.CacheKeys.Count + graph.Tables.Count + graph.Handlers.Count + graph.StoredProcedures.Count +
                                                        graph.Triggers.Count + graph.Views.Count + graph.Events.Count + graph.ExternalSources.Count;
    private static int Count(IReadOnlyDictionary<string, int> values, string key) => values.TryGetValue(key, out var value) ? value : 0;
    private static string KindName(UnresolvedKind kind) => kind switch
    {
        UnresolvedKind.CacheApi => "cache_api", UnresolvedKind.EventApi => "event_api", _ => kind.ToString().ToLowerInvariant()
    };
    private static string StableHash(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static RepositoryRevision GetRevision(string root)
    {
        var revision = Git(root, "rev-parse", "HEAD");
        if (revision.ExitCode != 0 || string.IsNullOrWhiteSpace(revision.Output)) return new RepositoryRevision(UnknownRevision, true);
        var status = Git(root, "status", "--porcelain");
        return new RepositoryRevision(revision.Output.Trim(), status.ExitCode == 0 && string.IsNullOrWhiteSpace(status.Output));
    }

    private static (int ExitCode, string Output) Git(string root, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add($"safe.directory={root.Replace('\\', '/')}");
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(root);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    /// <summary>
    /// What the manifest says the named file holds. The <c>files</c> block keys the expectation by file
    /// name, which is what makes the check meaningful: reading the kind out of the file being checked let
    /// a role sample renamed to <c>ef-write-sample.json</c> pass as a check of the ef-write sample.
    /// </summary>
    private static async Task<ManifestSample> ReadManifestForFileAsync(string samplePath, string? root)
    {
        var path = LocateManifest(root) ?? throw new FileNotFoundException($"Could not find '{MANIFEST}'.");
        var name = Path.GetFileName(samplePath);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"The metrics manifest has no 'files' block to look '{name}' up in.");

        var entry = files.EnumerateArray()
                         .FirstOrDefault(item => Text(item, "file", out var declared) &&
                                                 string.Equals(declared, name, StringComparison.Ordinal));
        if (entry.ValueKind != JsonValueKind.Object || !Text(entry, "sample", out var sample))
            throw new ArgumentException($"The metrics manifest names no sample for the file '{name}'.");

        return await ReadManifestAsync(sample, root).ConfigureAwait(false);
    }

    private static async Task<ManifestSample> ReadManifestAsync(string sample, string? root = null)
    {
        var path = LocateManifest(root) ?? throw new FileNotFoundException($"Could not find '{MANIFEST}'.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));
        var manifest = document.RootElement;
        var definition = manifest.TryGetProperty("samples", out var samples) ? samples : manifest;
        if (definition.ValueKind == JsonValueKind.Array)
            definition = definition.EnumerateArray().SingleOrDefault(item => item.TryGetProperty("sample", out var name) && name.GetString() == sample);
        else if (definition.ValueKind == JsonValueKind.Object && definition.TryGetProperty(sample, out var named))
            definition = named;
        if (definition.ValueKind != JsonValueKind.Object || !Number(definition, "targetCount", out var targetCount))
            throw new ArgumentException($"The metrics manifest has no '{sample}' sample.");
        var revision = Text(definition, "revision", out var localRevision) ? localRevision : Text(manifest, "revision", out var sharedRevision) ? sharedRevision : null;
        if (revision is null) throw new ArgumentException("The metrics manifest must record revision.");
        return new ManifestSample(sample, targetCount, revision);
    }

    private static string? LocateManifest(string? root)
    {
        foreach (var start in new[] { root, Environment.CurrentDirectory, AppContext.BaseDirectory }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!))
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, MANIFEST);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private static List<string> SampleDifferences(MetricsSample stored, MetricsSample observed, ManifestSample expected)
    {
        var differences = new List<string>();
        if (stored.Sample != expected.Sample) differences.Add("sample differs");
        if (stored.TargetCount != expected.TargetCount) differences.Add("targetCount differs");
        if (stored.CandidateCount != observed.CandidateCount) differences.Add("candidateCount differs");
        if (stored.Revision != observed.Revision) differences.Add("revision differs");
        if (stored.Rows.Count != observed.Rows.Count) differences.Add("rows length differs");
        foreach (var pair in stored.Rows.Zip(observed.Rows))
        {
            if (pair.First.Id != pair.Second.Id) differences.Add($"id differs: {pair.First.Id}");
            if (pair.First.File != pair.Second.File) differences.Add($"file differs: {pair.First.Id}");
            if (pair.First.Line != pair.Second.Line) differences.Add($"line differs: {pair.First.Id}");
            if (pair.First.Prediction != pair.Second.Prediction) differences.Add($"prediction differs: {pair.First.Id}");
        }
        return differences;
    }

    private static bool Text(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value = member.GetString()!);
    }
    private static bool Number(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var member) && member.TryGetInt32(out value) && value >= 0;
    }

    private enum MetricsMode { Measure, Sample, Compare, Score }
    private sealed record RepositoryRevision(string Value, bool WorkingTreeClean);
    private sealed record Indexed(CacheGraph Graph, double LoadSeconds, double IndexSeconds, int Diagnostics, LoadCoverage Coverage,
                                  RepositoryRevision Revision, string? RecognizersFile, string? RecognizersHash, int RecognizersApplied,
                                  string? WorkspaceFile, string? WorkspaceHash, int WorkspaceApplied);

    private sealed record WorkspaceDeclarations(IReadOnlyList<EventRecognizer> Events, IReadOnlyList<CacheRecognizer> Caches)
    {
        /// <summary>How many recognizers the workspace contributed, of either kind. It is what a reader
        /// checks to see that the file was read rather than merely named.</summary>
        public int Count => Events.Count + Caches.Count;
    }
    private sealed record ManifestSample(string Sample, int TargetCount, string Revision);

    private sealed class Options
    {
        public MetricsMode Mode { get; private init; }
        public string? Root { get; private init; }
        public string? Solution { get; private init; }
        public string? Out { get; private init; }
        public string? Recognizers { get; private init; }
        public string? Workspace { get; private init; }
        public string? Sample { get; private init; }
        public int? Count { get; private init; }
        public string? Compare { get; private init; }
        public string? Score { get; private init; }
        public bool Force { get; private init; }

        /// <summary>Set by <see cref="RunAsync"/> after parsing; not a command-line option.</summary>
        public SolutionLoad Load { get; set; } = (path, token) => new MsBuildSolutionLoader().LoadAsync(path, token);

        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var force = false;
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--force") { force = true; continue; }
                if (args[index] is not ("--root" or "--solution" or "--out" or "--recognizers" or "--workspace" or "--sample" or "--count" or "--compare" or "--score") ||
                    index + 1 >= args.Length || !values.TryAdd(args[index], args[++index]))
                    throw new ArgumentException("Usage: cachedet metrics --root <path> --solution <name> --out <file> [--recognizers <file>] [--workspace <file>].");
            }
            var sample = values.GetValueOrDefault("--sample");
            var compare = values.GetValueOrDefault("--compare");
            var score = values.GetValueOrDefault("--score");
            var modes = (sample is null ? 0 : 1) + (compare is null ? 0 : 1) + (score is null ? 0 : 1);
            if (modes > 1) throw new ArgumentException("Only one of --sample, --compare, and --score may be used.");
            var mode = sample is not null ? MetricsMode.Sample : compare is not null ? MetricsMode.Compare : score is not null ? MetricsMode.Score : MetricsMode.Measure;
            int? count = null;
            if (values.TryGetValue("--count", out var countText) && (!int.TryParse(countText, out var parsed) || parsed < 0))
                throw new ArgumentException("--count must be a non-negative integer.");
            else if (values.TryGetValue("--count", out countText)) count = int.Parse(countText);
            var options = new Options { Mode = mode, Root = values.GetValueOrDefault("--root"), Solution = values.GetValueOrDefault("--solution"),
                                        Out = values.GetValueOrDefault("--out"), Recognizers = values.GetValueOrDefault("--recognizers"),
                                        Workspace = values.GetValueOrDefault("--workspace"),
                                        Sample = sample, Count = count, Compare = compare, Score = score, Force = force };
            if (mode == MetricsMode.Score)
            {
                if (string.IsNullOrWhiteSpace(score) || values.Count != 1) throw new ArgumentException("Usage: cachedet metrics --score <file>.");
                return options;
            }
            if (string.IsNullOrWhiteSpace(options.Root) || string.IsNullOrWhiteSpace(options.Solution))
                throw new ArgumentException("--root and a non-empty --solution are required.");
            if (mode is MetricsMode.Measure or MetricsMode.Sample && string.IsNullOrWhiteSpace(options.Out))
                throw new ArgumentException("--out is required.");
            // A sample and a comparison are drawn from the corpus as the built-in analyser sees it; a
            // declaration file of either kind would make the drawn rows depend on it, so both refuse both.
            if (mode == MetricsMode.Sample && (count is null || options.Recognizers is not null || options.Workspace is not null))
                throw new ArgumentException("--sample requires --count and does not accept --recognizers or --workspace.");
            if (mode == MetricsMode.Compare && (string.IsNullOrWhiteSpace(compare) || options.Out is not null || options.Count is not null || options.Recognizers is not null || options.Workspace is not null || force))
                throw new ArgumentException("Usage: cachedet metrics --compare <file> --root <path> --solution <name>.");
            return options;
        }
    }
}
