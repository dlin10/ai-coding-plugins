using System.Text;
using System.Text.Json.Nodes;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>One whole-entry oracle member and the input used to generate it.</summary>
/// <param name="Member">The declaration id.</param>
/// <param name="Input">The implementation input.</param>
/// <param name="Truth">The whole-entry truth.</param>
/// <param name="Group">The reporting group.</param>
internal sealed record WholeEntryGold(string Member, EvalInput Input, EntryTruth Truth, string Group);

/// <summary>One generated whole-entry answer.</summary>
/// <param name="Gold">The oracle member.</param>
/// <param name="Answer">The generator's answer.</param>
/// <param name="DriverSynthesized">Whether the member's driver compiled.</param>
internal sealed record WholeEntryAnswer(WholeEntryGold Gold, GeneratedAnswer Answer, bool DriverSynthesized);

/// <summary>A whole-entry oracle run.</summary>
/// <param name="Answers">The answers, sorted by member id.</param>
/// <param name="Packages">The implementation assemblies used by the run.</param>
internal sealed record WholeEntryRun(IReadOnlyList<WholeEntryAnswer> Answers, IReadOnlyList<EvalPackage> Packages);

/// <summary>Shared execution and scoring for the effects and LINQ whole-entry oracles.</summary>
internal static class WholeEntryEvals
{
    internal static WholeEntryRun Run(ImplementationAssemblies resolver, IReadOnlyList<WholeEntryGold> gold)
    {
        var resolutions = new Dictionary<WholeEntryGold, ImplementationResolution>();
        foreach (var member in gold)
        {
            var input = member.Input;
            var resolution = resolver.Resolve(input.Assembly, input.Version, input.Package, input.Framework);
            if (resolution.Reason == GenerationReasons.NO_IMPLEMENTATION)
                throw new MissingEvalInputException($"{member.Member}: {resolution.Detail}");
            resolutions[member] = resolution;
        }

        var answers = ModelEvals.GenerateAll(gold, member => Generate(member, resolver))
                                .OrderBy(answer => answer.Gold.Member, StringComparer.Ordinal).ToArray();
        var packages = answers.Where(answer => resolutions[answer.Gold].Assembly is not null)
                              .GroupBy(answer => resolutions[answer.Gold].Assembly!.Path, StringComparer.OrdinalIgnoreCase)
                              .Select(group => Package(resolutions[group.First().Gold].Assembly!, resolutions[group.First().Gold].Reason,
                                                       group.ToArray()))
                              .OrderBy(package => package.Assembly, StringComparer.Ordinal)
                              .ThenBy(package => package.ImplementationVersion, StringComparer.Ordinal)
                              .ToArray();
        return new WholeEntryRun(answers, packages);
    }

    internal static IReadOnlyList<(WholeEntryAnswer Answer, EntryComparison Comparison)> Compared(WholeEntryRun run) =>
        run.Answers.Select(answer => (answer, EntryComparator.Compare(answer.Answer, answer.Gold.Truth))).ToArray();

    internal static int Exact(WholeEntryRun run) => Compared(run).Count(item => item.Comparison.IsExact);

    internal static IReadOnlyDictionary<string, int> ExactByGroup(WholeEntryRun run) =>
        Compared(run).GroupBy(item => item.Answer.Gold.Group, StringComparer.Ordinal)
                     .ToDictionary(group => group.Key, group => group.Count(item => item.Comparison.IsExact), StringComparer.Ordinal);

    private static WholeEntryAnswer Generate(WholeEntryGold member, ImplementationAssemblies resolver)
    {
        var input = member.Input;
        var trace = ModelGenerator.Trace(new GenerationRequest(input.Assembly, input.Version, member.Member, input.Package, input.Framework), resolver,
                                         CancellationToken.None);
        return new WholeEntryAnswer(member, trace.Answer, trace.Driver is not null);
    }

    private static EvalPackage Package(ImplementationAssembly assembly, string? reason, IReadOnlyList<WholeEntryAnswer> answers)
    {
        var library = reason is null ? LibraryCompilation.Compile(assembly, CancellationToken.None) : null;
        return new EvalPackage(assembly.AssemblyName, assembly.PackageId, assembly.ImplementationVersion, library?.Bodies ?? 0, library?.ExternBodies ?? 0,
                               answers.Count, answers.Count(answer => answer.DriverSynthesized));
    }
}

/// <summary>The 18-member effects oracle.</summary>
internal static class EffectEvals
{
    internal const int MEMBERS = 18;
    internal const int CORELIB_MEMBERS = 6;

    private static readonly Lazy<WholeEntryRun> Installed = new(() => WholeEntryEvals.Run(ImplementationAssemblies.ForThisProcess(), ReadGold()),
                                                                LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string GoldPath => Path.Combine(ModelEvals.ModelsDirectory, "effects-gold.json");

    internal static WholeEntryRun InstalledRun => Installed.Value;

    internal static IReadOnlyList<WholeEntryGold> ReadGold()
    {
        if (!File.Exists(GoldPath))
            throw new MissingEvalInputException($"the effects gold file {GoldPath} is missing");

        var entries = JsonNode.Parse(File.ReadAllText(GoldPath))!.AsArray();
        var gold = entries.Select(ReadMember).ToArray();
        if (gold.Length != MEMBERS || gold.Count(member => member.Input.Assembly == "System.Private.CoreLib") != CORELIB_MEMBERS)
            throw new MissingEvalInputException($"the effects gold file holds {gold.Length} members, " +
                                                $"{gold.Count(member => member.Input.Assembly == "System.Private.CoreLib")} of CoreLib");
        return gold;
    }

    private static WholeEntryGold ReadMember(JsonNode? node)
    {
        var entry = node?.AsObject() ?? throw new MissingEvalInputException("the effects gold file contains a null member");
        var member = (string)entry["id"]!;
        var assembly = (string)entry["assembly"]!;
        var version = (string)entry["version"]!;
        var input = Input(assembly, version);
        var safe = entry["safeModel"] ?? throw new MissingEvalInputException($"{member}: safeModel is missing");
        var truth = safe.GetValueKind() == System.Text.Json.JsonValueKind.String && safe.GetValue<string>() == "opaque"
            ? EntryTruth.Opaque
            : EntryTruth.Entry(ReadModel(member, assembly, safe.AsObject()));
        return new WholeEntryGold(member, input, truth, assembly);
    }

    private static EvalInput Input(string assembly, string version) => assembly switch
    {
        "System.Private.CoreLib" => new EvalInput(assembly, version, null, "net10.0"),
        "System.Console" or "System.Text.Json" or "System.Threading.Channels" when version == "10.0" =>
            new EvalInput(assembly, version, null, "net10.0"),
        "Google.Protobuf" when version == "3.21.9" => new EvalInput(assembly, version, "Google.Protobuf", "net8.0"),
        "Polly" when version == "7.2.3" => new EvalInput(assembly, version, "Polly", "net8.0"),
        "AutoMapper" when version == "14.0.0" => new EvalInput(assembly, version, "AutoMapper", "net8.0"),
        "FluentValidation" when version == "11.5.1" => new EvalInput(assembly, version, "FluentValidation", "net8.0"),
        _ => throw new MissingEvalInputException($"{assembly} {version} is not in the effects input table")
    };

    private static LibraryModel ReadModel(string member, string assembly, JsonObject safe)
    {
        var model = (JsonObject)safe.DeepClone();
        model["member"] = member;
        model["effects"] ??= new JsonObject();
        var file = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["assemblies"] = new JsonArray(assembly),
            ["models"] = new JsonArray(model)
        };
        var bytes = Encoding.UTF8.GetBytes(file.ToJsonString());
        var parsed = ProjectModelFiles.Read("effects-gold.json", bytes);
        if (parsed.Rejections.Count != 0 || parsed.Entries.Count != 1)
            throw new MissingEvalInputException($"{member}: invalid safeModel: {string.Join(", ", parsed.Rejections.Select(item => item.Reason))}");
        return Model(parsed.Entries[0]);
    }

    private static LibraryModel Model(ProjectModelEntry entry)
    {
        var versions = entry.Versions ?? (new Version(0, 0, 0, 0), new Version(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue));
        return new LibraryModel(entry.Member,
            entry.Assemblies.Select(assembly => new SupportedAssemblyVersion(assembly, versions.Item1, versions.Item2)).ToArray(), entry.Effects)
        {
            Result = entry.Result,
            Fates = entry.Fates,
            Stores = entry.Stores,
            Outputs = entry.Outputs,
            Keeps = entry.Keeps
        };
    }
}

/// <summary>The 129-entry built-in LINQ oracle, generated against the installed .NET 10 implementation assemblies.</summary>
internal static class LinqEvals
{
    internal const int MEMBERS = 129;
    internal const int ENUMERABLE_MEMBERS = 114;
    internal const int QUERYABLE_MEMBERS = 15;

    private static readonly Lazy<WholeEntryRun> Installed = new(() => WholeEntryEvals.Run(ImplementationAssemblies.ForThisProcess(), ReadGold()),
                                                                LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string GoldPath => RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "src", "ConcurrencyHunter.Core", "Providers",
                                                                           "LibraryModels", "BuiltIn", "linq.json");

    internal static WholeEntryRun InstalledRun => Installed.Value;

    internal static IReadOnlyList<WholeEntryGold> ReadGold()
    {
        if (!File.Exists(GoldPath))
            throw new MissingEvalInputException($"the LINQ model file {GoldPath} is missing");
        var models = BuiltInModelReader.Read(File.ReadAllBytes(GoldPath)).Members;
        var gold = models.Select(model =>
        {
            var assembly = model.Assemblies.Select(range => range.AssemblyName).Distinct(StringComparer.Ordinal).Single();
            return new WholeEntryGold(model.Id, new EvalInput(assembly, "10.0", null, "net10.0"), EntryTruth.Entry(model), assembly);
        }).ToArray();
        if (gold.Length != MEMBERS || gold.Count(member => member.Group == "System.Linq") != ENUMERABLE_MEMBERS ||
            gold.Count(member => member.Group == "System.Linq.Queryable") != QUERYABLE_MEMBERS)
            throw new MissingEvalInputException($"the LINQ oracle holds {gold.Length} entries, " +
                                                $"{gold.Count(member => member.Group == "System.Linq")} Enumerable and " +
                                                $"{gold.Count(member => member.Group == "System.Linq.Queryable")} Queryable");
        return gold;
    }
}
