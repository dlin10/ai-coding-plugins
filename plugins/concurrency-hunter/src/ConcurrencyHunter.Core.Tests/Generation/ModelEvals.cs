using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>Runs a test only when <c>CH_MODEL_EVALS=1</c>: the model evals read the installed shared framework and the NuGet global
/// packages folder, and under the flag a missing input fails rather than skips.</summary>
public sealed class RequiresModelEvalsFactAttribute : FactAttribute
{
    public const string VARIABLE = "CH_MODEL_EVALS";

    public RequiresModelEvalsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(VARIABLE) != "1")
            Skip = $"{VARIABLE} is not 1. Set it to run the model evals against the installed .NET 8 and the NuGet packages folder.";
    }
}

/// <summary>A gold member (<c>skills/hunt/evals/models/gold.json</c>).</summary>
/// <param name="Key">The member's short key.</param>
/// <param name="Id">The declaration id.</param>
/// <param name="Label">The gold label, in the research names.</param>
/// <param name="Holder">The holder kind of a <c>holder</c> label, else <c>null</c>.</param>
/// <param name="DelegateParams">The gold parameters.</param>
/// <param name="Assembly">The assembly.</param>
/// <param name="Version">The version.</param>
internal sealed record GoldMember(string Key, string Id, string Label, string? Holder, IReadOnlyList<string> DelegateParams, string Assembly,
                                  string Version)
{
    /// <summary>The gold fate in the vocabulary of TD-034b.</summary>
    public ClassifiedFate Fate => ModelEvals.GoldFate(Label, Holder);
}

/// <summary>How one gold assembly is asked for (G-7's input table): the gold files carry no package ids or platforms.</summary>
/// <param name="Assembly">The assembly name.</param>
/// <param name="Version">The version.</param>
/// <param name="Package">The package id, or <c>null</c> for a framework assembly.</param>
/// <param name="Framework">The platform.</param>
internal sealed record EvalInput(string Assembly, string Version, string? Package, string Framework);

/// <summary>One gold member's answer and what produced it.</summary>
/// <param name="Gold">The gold member.</param>
/// <param name="Answer">The generator's answer.</param>
/// <param name="DriverSynthesized">Whether the member's driver compiled.</param>
internal sealed record EvalAnswer(GoldMember Gold, GeneratedAnswer Answer, bool DriverSynthesized);

/// <summary>What one implementation assembly showed (G-7's <c>packages</c>).</summary>
/// <param name="Assembly">The assembly name.</param>
/// <param name="Package">The package id, or <c>null</c>.</param>
/// <param name="ImplementationVersion">The version folder the resolver took.</param>
/// <param name="Bodies">The method bodies of the library compilation.</param>
/// <param name="ExternBodies">The bodies made <c>extern</c>.</param>
/// <param name="Members">The gold members of the assembly.</param>
/// <param name="DriversSynthesized">The members whose driver compiled.</param>
internal sealed record EvalPackage(string Assembly, string? Package, string ImplementationVersion, int Bodies, int ExternBodies, int Members,
                                   int DriversSynthesized);

/// <summary>The model evals' answers.</summary>
/// <param name="Answers">The answers, sorted by member id.</param>
/// <param name="Packages">The implementation assemblies, sorted by assembly name then implementation version.</param>
internal sealed record EvalRun(IReadOnlyList<EvalAnswer> Answers, IReadOnlyList<EvalPackage> Packages);

/// <summary>A model eval input that is not on this machine: under the flag the evals fail on it.</summary>
/// <param name="message">What is missing.</param>
internal sealed class MissingEvalInputException(string message) : Exception(message);

/// <summary>The model evals of G-7: the comparator, the gold set and its inputs, the generator run on them and the snapshot.</summary>
internal static class ModelEvals
{
    public const string RECORD_VARIABLE = "CH_MODEL_EVALS_RECORD";
    public const int GOLD_MEMBERS = 18;
    public const int GOLD_PARAMETERS = 24;
    public const int EXACT_FLOOR = 14;

    public static readonly ClassifiedFate Unknown = new(FateClassifier.UNKNOWN_EXECUTION, null);
    public static readonly ClassifiedFate HolderResult = new(FateClassifier.HOLDER, FateClassifier.RESULT);

    /// <summary>G-7's input table, by assembly.</summary>
    public static readonly IReadOnlyDictionary<string, EvalInput> Inputs = new[]
    {
        new EvalInput("System.Linq", "8.0", null, "net8.0"),
        new EvalInput("System.Security.Claims", "8.0", null, "net8.0"),
        new EvalInput("Polly", "7.2.3", "Polly", "net8.0"),
        new EvalInput("Google.Protobuf", "3.21.9", "Google.Protobuf", "net8.0")
    }.ToDictionary(input => input.Assembly, StringComparer.Ordinal);

    private static readonly Lazy<EvalRun> Installed = new(() => Run(ImplementationAssemblies.ForThisProcess(), GoldPath),
                                                          LazyThreadSafetyMode.ExecutionAndPublication);

    public static string ModelsDirectory => RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "skills", "hunt", "evals", "models");

    public static string GoldPath => Path.Combine(ModelsDirectory, "gold.json");

    public static string SnapshotPath => Path.Combine(ModelsDirectory, "generator-snapshot.json");

    /// <summary>The evals run on this machine's shared framework and packages folder, once per process.</summary>
    public static EvalRun InstalledRun => Installed.Value;

    // ---- the comparator ----

    /// <summary>A gold label in the vocabulary: <c>framework-event</c> is <c>unknown-execution</c>, <c>di-registration</c> is
    /// <c>di-factory</c>, a <c>holder</c> keeps its holder kind.</summary>
    /// <param name="label">The gold label.</param>
    /// <param name="holder">The gold holder kind, or <c>null</c>.</param>
    public static ClassifiedFate GoldFate(string label, string? holder) => label switch
    {
        "framework-event" => Unknown,
        "di-registration" => new ClassifiedFate("di-factory", null),
        FateClassifier.HOLDER => new ClassifiedFate(label, holder),
        _ => new ClassifiedFate(label, null)
    };

    /// <summary>The fate an answer gives a parameter; a member with no classification, or a parameter it did not classify, counts as
    /// <c>unknown-execution</c>.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="parameter">The gold parameter.</param>
    public static ClassifiedFate Answered(GeneratedAnswer answer, string parameter) => answer.Classified?.GetValueOrDefault(parameter) ?? Unknown;

    /// <summary>Whether a fate is no unsafe narrowing of the gold one: the gold fate with the gold holder kind, <c>unknown-execution</c>,
    /// or <c>holder</c> <c>result</c> where the gold is <c>iterator</c>. Anything else — a <c>holder</c> of the wrong kind included —
    /// is unsafe.</summary>
    /// <param name="answered">The classified fate.</param>
    /// <param name="gold">The gold fate.</param>
    public static bool IsSafe(ClassifiedFate answered, ClassifiedFate gold) =>
        answered == gold || answered == Unknown || answered == HolderResult && gold.Fate == FateClassifier.ITERATOR;

    /// <summary>Whether a fate is the gold one, holder kind included.</summary>
    /// <param name="answered">The classified fate.</param>
    /// <param name="gold">The gold fate.</param>
    public static bool IsExact(ClassifiedFate answered, ClassifiedFate gold) => answered == gold;

    public static string Show(ClassifiedFate fate) => fate.Holder is null ? fate.Fate : $"{fate.Fate} {fate.Holder}";

    /// <summary>The number of gold parameters a run classified exactly.</summary>
    /// <param name="run">The run.</param>
    public static int Exact(EvalRun run) =>
        run.Answers.Sum(answer => answer.Gold.DelegateParams.Count(parameter => IsExact(Answered(answer.Answer, parameter), answer.Gold.Fate)));

    /// <summary>The <c>exact</c> count a snapshot recorded, or <c>null</c> when it records none.</summary>
    /// <param name="snapshot">The snapshot's text.</param>
    public static int? RecordedExact(string snapshot) => (int?)JsonNode.Parse(snapshot)!["exact"];

    // ---- the gold set ----

    /// <summary>The gold members of the four assemblies the evals measure, as the gold file lists them.</summary>
    /// <param name="goldPath">The gold file.</param>
    /// <exception cref="MissingEvalInputException">The gold file is missing.</exception>
    public static IReadOnlyList<GoldMember> ReadGold(string goldPath)
    {
        if (!File.Exists(goldPath))
            throw new MissingEvalInputException($"the gold file {goldPath} is missing");
        return JsonNode.Parse(File.ReadAllText(goldPath))!.AsArray()
                       .Select(entry => new GoldMember((string)entry!["key"]!, (string)entry["id"]!, (string)entry["label"]!, (string?)entry["holder"],
                                                       entry["delegateParams"]!.AsArray().Select(parameter => (string)parameter!).ToArray(),
                                                       (string)entry["assembly"]!, (string)entry["version"]!))
                       .Where(member => Inputs.ContainsKey(member.Assembly))
                       .ToArray();
    }

    // ---- the run ----

    /// <summary>Runs the generator on every gold member of the input table. Every input is resolved first, so a missing framework or
    /// package fails before anything is generated, never as a member counted <c>unknown-execution</c>.</summary>
    /// <param name="resolver">Finds the implementation assemblies.</param>
    /// <param name="goldPath">The gold file.</param>
    /// <exception cref="MissingEvalInputException">The gold file, a shared framework or a package is missing.</exception>
    public static EvalRun Run(ImplementationAssemblies resolver, string goldPath)
    {
        var gold = ReadGold(goldPath);
        var parameters = gold.Sum(member => member.DelegateParams.Count);
        if (gold.Count != GOLD_MEMBERS || parameters != GOLD_PARAMETERS)
        {
            throw new MissingEvalInputException($"the gold file holds {gold.Count} members with {parameters} parameters of the input table's " +
                                                $"assemblies, not {GOLD_MEMBERS} with {GOLD_PARAMETERS}");
        }

        var resolutions = new Dictionary<GoldMember, ImplementationResolution>();
        foreach (var member in gold)
        {
            var input = Inputs[member.Assembly];
            if (member.Version != input.Version)
                throw new MissingEvalInputException($"{member.Id}: the gold version {member.Version} is not the input table's {input.Version}");
            var resolution = resolver.Resolve(input.Assembly, input.Version, input.Package, input.Framework);
            if (resolution.Reason == GenerationReasons.NO_IMPLEMENTATION)
                throw new MissingEvalInputException($"{member.Id}: {resolution.Detail}");
            resolutions[member] = resolution;
        }

        var answers = gold.Select(member => Generate(member, resolver)).OrderBy(answer => answer.Gold.Id, StringComparer.Ordinal).ToArray();
        var packages = answers.GroupBy(answer => resolutions[answer.Gold].Assembly!.Path, StringComparer.OrdinalIgnoreCase)
                              .Select(group => Package(resolutions[group.First().Gold].Assembly!, resolutions[group.First().Gold].Reason, group.ToArray()))
                              .OrderBy(package => package.Assembly, StringComparer.Ordinal)
                              .ThenBy(package => package.ImplementationVersion, StringComparer.Ordinal)
                              .ToArray();
        return new EvalRun(answers, packages);
    }

    private static EvalAnswer Generate(GoldMember member, ImplementationAssemblies resolver)
    {
        var input = Inputs[member.Assembly];
        var trace = ModelGenerator.Trace(new GenerationRequest(input.Assembly, input.Version, member.Id, input.Package, input.Framework), resolver,
                                         CancellationToken.None);
        return new EvalAnswer(member, trace.Answer, trace.Driver is not null);
    }

    private static EvalPackage Package(ImplementationAssembly assembly, string? reason, IReadOnlyList<EvalAnswer> answers)
    {
        // Compiled once per process: the generation of the members already built this compilation.
        var library = reason is null ? LibraryCompilation.Compile(assembly, CancellationToken.None) : null;
        return new EvalPackage(assembly.AssemblyName, assembly.PackageId, assembly.ImplementationVersion, library?.Bodies ?? 0, library?.ExternBodies ?? 0,
                               answers.Count, answers.Count(answer => answer.DriverSynthesized));
    }

    // ---- the snapshot ----

    /// <summary>The snapshot of a run: UTF-8 without BOM, LF, two-space indentation, a final newline.</summary>
    /// <param name="run">The run.</param>
    public static string Snapshot(EvalRun run)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
               }))
        {
            json.WriteStartObject();
            json.WriteStartArray("members");
            foreach (var answer in run.Answers)
                WriteMember(json, answer.Answer);
            json.WriteEndArray();
            json.WriteStartArray("packages");
            foreach (var package in run.Packages)
            {
                json.WriteStartObject();
                json.WriteString("assembly", package.Assembly);
                json.WriteString("package", package.Package);
                json.WriteString("implementationVersion", package.ImplementationVersion);
                json.WriteNumber("bodies", package.Bodies);
                json.WriteNumber("externBodies", package.ExternBodies);
                json.WriteNumber("members", package.Members);
                json.WriteNumber("driversSynthesized", package.DriversSynthesized);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteNumber("exact", Exact(run));
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteMember(Utf8JsonWriter json, GeneratedAnswer answer)
    {
        json.WriteStartObject();
        json.WriteString("member", answer.Member);
        if (answer.Generation.Implementation is { } implementation)
        {
            json.WriteStartObject("implementation");
            json.WriteString("assemblyVersion", implementation.AssemblyVersion);
            json.WriteString("fileVersion", implementation.FileVersion);
            json.WriteString("mvid", implementation.Mvid);
            json.WriteEndObject();
        }
        else
            json.WriteNull("implementation");
        if (answer.Classified is { } classified)
        {
            json.WriteStartObject("classified");
            foreach (var (parameter, fate) in classified.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                json.WriteStartObject(parameter);
                json.WriteString("fate", fate.Fate);
                json.WriteString("holder", fate.Holder);
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }
        else
            json.WriteNull("classified");
        json.WriteString("reason", answer.Reason);
        json.WriteNumber("reachedBodies", answer.Generation.ReachedBodies);
        json.WriteEndObject();
    }

    /// <summary>How a fresh snapshot differs from the recorded one: implementation identity changes (a runtime or package update) first,
    /// then answer differences; empty when they are the same.</summary>
    /// <param name="recorded">The recorded snapshot's text.</param>
    /// <param name="fresh">The fresh snapshot's text.</param>
    public static IReadOnlyList<string> Differences(string recorded, string fresh)
    {
        var before = JsonNode.Parse(recorded)!;
        var after = JsonNode.Parse(fresh)!;
        var beforeMembers = Keyed(before["members"], "member");
        var afterMembers = Keyed(after["members"], "member");
        var beforePackages = Keyed(before["packages"], "assembly");
        var afterPackages = Keyed(after["packages"], "assembly");

        var identity = new List<string>();
        foreach (var (id, member) in afterMembers)
        {
            if (beforeMembers.TryGetValue(id, out var old) && !JsonNode.DeepEquals(old["implementation"], member["implementation"]))
                identity.Add($"implementation of {id}: {old["implementation"]?.ToJsonString() ?? "null"} -> {member["implementation"]?.ToJsonString() ?? "null"}");
        }
        foreach (var (assembly, package) in afterPackages)
        {
            if (beforePackages.TryGetValue(assembly, out var old) &&
                (string?)old["implementationVersion"] != (string?)package["implementationVersion"])
                identity.Add($"implementation version of {assembly}: {old["implementationVersion"]} -> {package["implementationVersion"]}");
        }
        if (identity.Count > 0)
        {
            return ["The implementation identity changed (a runtime or package update): re-record on purpose with CH_MODEL_EVALS=1 " +
                    $"{RECORD_VARIABLE}=1.", .. identity];
        }

        var answers = new List<string>();
        foreach (var id in beforeMembers.Keys.Union(afterMembers.Keys).Order(StringComparer.Ordinal))
        {
            var old = beforeMembers.GetValueOrDefault(id);
            var now = afterMembers.GetValueOrDefault(id);
            if (!JsonNode.DeepEquals(old, now))
                answers.Add($"{id}: recorded {old?.ToJsonString() ?? "nothing"}, now {now?.ToJsonString() ?? "nothing"}");
        }
        foreach (var assembly in beforePackages.Keys.Union(afterPackages.Keys).Order(StringComparer.Ordinal))
        {
            var old = beforePackages.GetValueOrDefault(assembly);
            var now = afterPackages.GetValueOrDefault(assembly);
            if (!JsonNode.DeepEquals(old, now))
                answers.Add($"package {assembly}: recorded {old?.ToJsonString() ?? "nothing"}, now {now?.ToJsonString() ?? "nothing"}");
        }
        if (!JsonNode.DeepEquals(before["exact"], after["exact"]))
            answers.Add($"exact: recorded {before["exact"]?.ToJsonString() ?? "nothing"}, now {after["exact"]?.ToJsonString() ?? "nothing"}");
        if (answers.Count == 0 && Normalize(recorded) != Normalize(fresh))
            answers.Add("the recorded text differs from the fresh one in order or layout");
        return answers;
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static Dictionary<string, JsonNode> Keyed(JsonNode? array, string key) =>
        (array?.AsArray() ?? []).Where(entry => entry is not null).ToDictionary(entry => (string)entry![key]!, entry => entry!, StringComparer.Ordinal);
}
