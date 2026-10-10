using System.Diagnostics;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>What the generator is asked for: one member of one assembly.</summary>
/// <param name="AssemblyName">The assembly name.</param>
/// <param name="Version">The assembly version of a framework member, the package version with a package id.</param>
/// <param name="MemberId">The member's declaration id.</param>
/// <param name="PackageId">The package that ships the assembly; <c>null</c> for a framework member.</param>
/// <param name="Framework">The platform <c>net&lt;major&gt;.0</c> of a package member; <c>null</c> for the running runtime's.</param>
public sealed record GenerationRequest(string AssemblyName, string Version, string MemberId, string? PackageId, string? Framework);

/// <summary>The assembly as it was asked for.</summary>
/// <param name="Name">The assembly name.</param>
/// <param name="Version">The version.</param>
/// <param name="Package">The package id, or <c>null</c>.</param>
public sealed record GenerationAssembly(string Name, string Version, string? Package);

/// <summary>The implementation assembly the generation opened.</summary>
/// <param name="Path">The file.</param>
/// <param name="AssemblyVersion">The assembly version.</param>
/// <param name="FileVersion">The file version.</param>
/// <param name="Mvid">The module version id.</param>
public sealed record GenerationImplementation(string Path, string AssemblyVersion, string FileVersion, string Mvid);

/// <summary>What the generation used and saw (G-6).</summary>
/// <param name="Implementation">The implementation assembly, or <c>null</c> until one was opened.</param>
/// <param name="Framework">The platform used, or <c>null</c> when none was chosen.</param>
/// <param name="MissingDependencies">The package dependencies skipped.</param>
/// <param name="ExternBodies">The bodies the library compilation made <c>extern</c>.</param>
/// <param name="DefaultedValues">The non-holding values the driver filled with a default, by parameter name.</param>
/// <param name="Refusals">By parameter, the applicability rule that refused the fate the probes showed.</param>
/// <param name="SetupWidened">The parameters with a probe setup fired or handed to a call the engine could not follow.</param>
/// <param name="Seeds">The seed statements the driver wrote.</param>
/// <param name="Unseeded">The sorted field paths setup could not seed or did not reach.</param>
/// <param name="HolderTriggers">The trigger members observed for each holder delegate parameter.</param>
/// <param name="ReachedBodies">The bodies the reachable set reached, <c>0</c> before the pipeline ran.</param>
/// <param name="Seconds">The wall time of the whole generation, rounded to 0.1.</param>
public sealed record GenerationRecord(GenerationImplementation? Implementation, string? Framework, IReadOnlyList<string> MissingDependencies,
                                      int ExternBodies, IReadOnlyList<string> DefaultedValues, IReadOnlyDictionary<string, string> Refusals,
                                      IReadOnlyList<string> SetupWidened, int Seeds, IReadOnlyList<string> Unseeded,
                                      IReadOnlyDictionary<string, IReadOnlyList<string>> HolderTriggers, int ReachedBodies, double Seconds);

/// <summary>The generator's answer for one member (G-6): its classifications and whole model entry, or why either is absent.</summary>
/// <param name="SchemaVersion">The answer's schema version, <c>2</c>.</param>
/// <param name="Member">The declaration id asked for.</param>
/// <param name="Assembly">The assembly as asked for.</param>
/// <param name="Classified">The fates by parameter name, or <c>null</c> when nothing was classified.</param>
/// <param name="Reason">A reason of <see cref="GenerationReasons"/> when nothing was classified, else <c>null</c>.</param>
/// <param name="Model">The generated library-model entry, or <c>null</c>.</param>
/// <param name="ModelReason">A reason of <see cref="ModelReasons"/> when a classified member has no entry, else <c>null</c>.</param>
/// <param name="Generation">What the generation used and saw.</param>
public sealed record GeneratedAnswer(int SchemaVersion, string Member, GenerationAssembly Assembly, IReadOnlyDictionary<string, ClassifiedFate>? Classified,
                                     string? Reason, LibraryModel? Model, string? ModelReason, GenerationRecord Generation)
{
    /// <summary>Why, in words, for a reason: the claiming recognizer, the errors that made the library unusable, the stopped count.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>An answer with the driver and the engine run behind it, for tests.</summary>
/// <param name="Answer">The answer.</param>
/// <param name="Driver">The driver, when one was synthesized.</param>
/// <param name="Run">The fate run, when the pipeline ran and was not stopped.</param>
internal sealed record GenerationTrace(GeneratedAnswer Answer, Driver? Driver, ScopeRun? Run)
{
    /// <summary>A holder's confirmation run (A5), when one ran and did not throw; it may be stopped.</summary>
    public ScopeRun? Confirmation { get; init; }

    /// <summary>The stores into library state the effect reading recorded, when the generation read effects; empty otherwise.</summary>
    public IReadOnlyList<StateStore> StateStores { get; init; } = [];
}

/// <summary>The <b>Model generator</b> of SPEC TD-034b: finds a member's implementation assembly, decompiles and compiles it,
/// synthesizes a driver, analyses both with the run's own pipeline, classifies the fates and builds one whole model entry. It reads no
/// project models, writes no file and makes no network call.</summary>
public static class ModelGenerator
{
    private const int SCHEMA_VERSION = 2;
    private const int CLOSURE_BOUND = 1500;
    private const string SCOPE_ID = "model-generator";

    /// <summary>Whether a text is a declaration id the generator can be asked for: one the reader's grammar accepts (question 44),
    /// never the member pattern <c>(*)</c>. The grammar knows only <c>T:</c> and <c>M:</c>; a property's, an event's or a field's id
    /// is read as a member's and a namespace's as a type's, so those kinds reach the generator rather than a usage error: a
    /// <c>P:</c> or <c>E:</c> id is refused with <c>accessor</c> (G-6: after <c>member-not-found</c>, before
    /// <c>engine-recognized</c>), its detail naming the <c>M:</c> ids of the accessors, which are generated as methods are; an
    /// <c>F:</c> id is refused with <c>driver-not-synthesized</c>.</summary>
    /// <param name="id">The text.</param>
    public static bool IsMemberId(string? id)
    {
        if (id is not { Length: > 2 } || id[1] != ':')
            return false;
        var grammar = id[0] switch
        {
            'M' or 'P' or 'E' or 'F' => "M" + id[1..],
            'T' or 'N' => "T" + id[1..],
            _ => null
        };
        return grammar is not null && DeclarationId.Parse(grammar) is { IsPattern: false };
    }

    /// <summary>The answer for one member.</summary>
    /// <param name="request">The member asked for.</param>
    /// <param name="resolver">Finds the implementation assembly.</param>
    /// <param name="cancellationToken">Cancels the generation.</param>
    /// <exception cref="ArgumentException">The version or the framework does not parse.</exception>
    public static GeneratedAnswer Generate(GenerationRequest request, ImplementationAssemblies resolver, CancellationToken cancellationToken) =>
        Trace(request, resolver, cancellationToken).Answer;

    /// <summary>The answer for one member, with the driver and the run behind it.</summary>
    /// <param name="request">The member asked for.</param>
    /// <param name="resolver">Finds the implementation assembly.</param>
    /// <param name="cancellationToken">Cancels the generation.</param>
    internal static GenerationTrace Trace(GenerationRequest request, ImplementationAssemblies resolver, CancellationToken cancellationToken)
    {
        var facts = new Facts(request);
        var resolution = resolver.Resolve(request.AssemblyName, request.Version, request.PackageId, request.Framework);
        // The platform is the one the resolver chose, also when it found no assembly there.
        facts.Framework = resolution.Framework;
        if (resolution.Assembly is { } opened)
        {
            facts.Implementation = new GenerationImplementation(opened.Path, opened.AssemblyVersion, opened.FileVersion, opened.Mvid.ToString());
            facts.MissingDependencies = opened.MissingDependencies;
        }

        if (resolution.Reason is { } reason)
            return facts.Refused(reason, resolution.Detail);
        var library = LibraryCompilation.Compile(resolution.Assembly!, cancellationToken);
        return Classify(facts, library, cancellationToken);
    }

    /// <summary>The answer for one member of a library already compiled, from the member check on: no implementation assembly, no
    /// framework.</summary>
    /// <param name="request">The member asked for.</param>
    /// <param name="library">The library compilation.</param>
    /// <param name="cancellationToken">Cancels the generation.</param>
    /// <param name="models">The models the runs use in place of the built-in ones outside the library, for tests of a dependency's
    /// model; <c>null</c> for those.</param>
    internal static GenerationTrace Trace(GenerationRequest request, LibraryCompilationResult library, CancellationToken cancellationToken,
                                          LibraryModels? models = null) =>
        Classify(new Facts(request), library, cancellationToken, models);

    private static GenerationTrace Classify(Facts facts, LibraryCompilationResult library, CancellationToken cancellationToken,
                                            LibraryModels? models = null)
    {
        facts.ExternBodies = library.ExternBodies;
        if (library.Compilation is not { } compilation)
            return facts.Refused(library.Reason ?? GenerationReasons.LIBRARY_DOES_NOT_COMPILE, string.Join(", ", library.Errors));
        if (DriverSynthesizer.FindMember(compilation, facts.Request.MemberId) is not { } member)
            return facts.Refused(GenerationReasons.MEMBER_NOT_FOUND, $"{compilation.AssemblyName} declares no {facts.Request.MemberId}");
        if (DriverSynthesizer.IsAccessor(member))
            return facts.Refused(GenerationReasons.ACCESSOR, DriverSynthesizer.AccessorDetail(member));
        var corelib = compilation.AssemblyName == "System.Private.CoreLib";
        var claim = corelib
            ? member is IMethodSymbol method && EngineClaims.Of(method) is { } recognizer
                ? new EngineClaim(facts.Request.MemberId, recognizer) : null
            : EngineClaims.FirstIn(compilation.Assembly);
        if (claim is not null)
            return facts.Refused(GenerationReasons.ENGINE_RECOGNIZED, $"{claim.Method} is claimed by the {claim.Recognizer} recognizer");

        var synthesis = DriverSynthesizer.Synthesize(compilation, member, library.ExternMembers, library.OpenedFields, cancellationToken);
        if (synthesis.Driver is not { } driver)
            return facts.Refused(synthesis.Reason!, synthesis.Detail);
        facts.DefaultedValues = driver.DefaultedValues;
        facts.Seeds = driver.SeedStatements.Count;
        facts.Unseeded = driver.Unseeded;

        models ??= ModelsOutside(compilation.AssemblyName!);
        var loadedStatics = new HashSet<(string Type, string Name)>();
        var lowered = new Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?>();
        var reachability = corelib
            ? new ReachabilityRules(DispatchRule.ConstructedTypes, new HashSet<string>(StringComparer.Ordinal) { "System.Private.CoreLib:System.SR" })
            : null;
        ScopeRun Pipeline(bool triggers) =>
            ScopePipeline.Run(SCOPE_ID, [compilation, driver.Compilation], [], Path.GetTempPath(), new ProviderRegistry([new DriverRootProvider(triggers)]),
                              models, AnalysisLimits.Default, lowered, CLOSURE_BOUND, cancellationToken, reachability: reachability);

        ScopeRun run;
        // The lowering cache is shared by the runs, so a dropped body is reported only by the run that first lowered it.
        var dropped = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                run = Pipeline(triggers: false);
                dropped.UnionWith(run.LoweringDiagnostics);
                run = run with { LoweringDiagnostics = dropped.ToArray() };
                var previousCount = loadedStatics.Count;
                loadedStatics.UnionWith(SeedableFields.LoadedStatics(run).Where(field =>
                    field.Type.StartsWith(compilation.AssemblyName + ":", StringComparison.Ordinal)));
                if (run.Stopped || loadedStatics.Count == previousCount)
                    break;
                synthesis = DriverSynthesizer.Synthesize(compilation, member, library.ExternMembers, library.OpenedFields, loadedStatics,
                                                         cancellationToken);
                if (synthesis.Driver is not { } seededDriver)
                    return facts.Refused(synthesis.Reason!, synthesis.Detail);
                driver = seededDriver;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return facts.Refused(GenerationReasons.ANALYSIS_FAILED, $"the engine threw {error.GetType().Name}: {error.Message}", driver);
        }

        facts.DefaultedValues = driver.DefaultedValues;
        facts.Seeds = driver.SeedStatements.Count;
        facts.Unseeded = driver.Unseeded;
        facts.ReachedBodies = run.Reachable.ReachedBodies.Count;
        if (run.Stopped)
        {
            return facts.Refused(GenerationReasons.CLOSURE_BOUND, $"the reachable set reached {run.StoppedAtReachableBodies} bodies, over {CLOSURE_BOUND}",
                                 driver);
        }

        var body = IrLowering.RootBodyId(driver.Member);
        if (run.LoweringDiagnostics.FirstOrDefault(diagnostic => diagnostic.StartsWith($"lowering: {body}:", StringComparison.Ordinal)) is { } own)
            return facts.Refused(GenerationReasons.ANALYSIS_FAILED, own, driver, run);

        FateClassification classification;
        try
        {
            classification = FateClassifier.Classify(driver, run);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return facts.Refused(GenerationReasons.ANALYSIS_FAILED, $"the classification threw {error.GetType().Name}: {error.Message}", driver, run);
        }

        facts.Refusals = classification.Refusals;
        facts.SetupWidened = classification.SetupWidened;
        var classified = classification.Classified;
        ScopeRun? confirmation = null;
        // A holder is confirmed by a run that roots the triggers too (A5). A holder the triggers do not cover is unconfirmed whatever
        // that run shows, so it runs only when one of the holders found is covered.
        var holders = classified.Values.Where(fate => fate.Fate == FateClassifier.HOLDER).Select(fate => fate.Holder!).ToArray();
        if (holders.Length > 0)
        {
            if (holders.Any(driver.Covers))
            {
                try
                {
                    confirmation = Pipeline(triggers: true);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    confirmation = null;
                }
            }

            classified = FateClassifier.Confirmed(driver, classified, confirmation);
        }

        var effects = new EffectReader(driver, run, classified);
        var values = new ValueProvenance(driver, run, classified, confirmation);
        facts.Unseeded = driver.Unseeded.Concat(effects.UnreachedSeeds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        facts.HolderTriggers = values.HolderTriggers;

        var modelReason = effects.Reason is GenerationReasons.UNKNOWN_TOUCH or GenerationReasons.INCOMPLETE ? effects.Reason
            : HasLibraryState(effects, values) ? ModelReasons.LIBRARY_STATE
            : effects.Reason == GenerationReasons.VOCABULARY || values.Reason == GenerationReasons.VOCABULARY ? ModelReasons.VOCABULARY
            : null;
        var detail = "";
        LibraryModel? model = null;
        if (modelReason is null)
        {
            var built = BuildModel(driver.Member, classified, effects, values, out modelReason);
            if (modelReason is null)
            {
                var files = ProjectModelFiles.Read("generated.json", ModelEntryWriter.File(built!));
                detail = files.Rejections.FirstOrDefault()?.Reason ??
                         (files.Entries.Count == 1 ? ProjectModelResolver.EntryRejection(files.Entries[0], driver.Member, compilation) :
                          $"the generated file contains {files.Entries.Count} entries") ?? "";
                if (detail.Length == 0)
                    model = built;
                else
                    modelReason = ModelReasons.VOCABULARY;
            }
        }

        return new GenerationTrace(facts.Answer(classified, null, detail, model, modelReason), driver, run)
        {
            Confirmation = confirmation,
            StateStores = effects.StateStores
        };
    }

    private static LibraryModel? BuildModel(IMethodSymbol member, IReadOnlyDictionary<string, ClassifiedFate> classified, EffectReader reader,
                                            ValueProvenance values, out string? reason)
    {
        var fates = member.Parameters.Where(parameter => TypeShape.Of(parameter.Type) == TypeShapeKind.Delegate &&
                                                        classified.ContainsKey(parameter.Name))
                          .Select(parameter => Fate(parameter.Name, classified[parameter.Name], values.Inputs))
                          .ToArray();
        var effects = new List<LibraryEffect>();
        foreach (var (parameter, observed) in reader.Effects)
        {
            foreach (var effect in observed)
            {
                // TD-034a lets `this` carry only writes-cells. A read of the receiver that reaches a seed — a user object a program
                // stored there — is a real effect the vocabulary cannot name; every other receiver effect is the library's own state.
                if (parameter == FateClassifier.THIS && effect.Kind != EffectReader.WRITES_CELLS)
                {
                    if (effect.ReadsSeed)
                    {
                        reason = ModelReasons.VOCABULARY;
                        return null;
                    }
                    continue;
                }
                if (effect.Kind == EffectReader.READS_DEEP && EnumerationReadIsNamed(parameter, effect, fates, values))
                    continue;
                if (effect.Roots.Contains(DriverSynthesizer.ENUMERATE, StringComparer.Ordinal) &&
                    values.Result?.Leaf.Kind != LibraryResultKind.Sequence)
                {
                    reason = ModelReasons.VOCABULARY;
                    return null;
                }
                effects.Add(new LibraryEffect(effect.Kind switch
                {
                    EffectReader.READS_DEEP => LibraryEffectKind.DeepRead,
                    EffectReader.WRITES_ARGUMENT => LibraryEffectKind.WriteArgument,
                    EffectReader.WRITES_CELLS => LibraryEffectKind.WriteCells,
                    _ => throw new UnreachableException($"Unknown generated effect {effect.Kind}.")
                }, parameter));
            }
        }

        var version = member.ContainingAssembly.Identity.Version;
        var maximum = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision) + 1);
        reason = null;
        return new LibraryModel(member.GetDocumentationCommentId()!,
                                [new SupportedAssemblyVersion(member.ContainingAssembly.Identity.Name, version, maximum)], effects)
        {
            Result = values.Result,
            Fates = fates,
            Keeps = values.Keeps,
            Outputs = values.Outputs,
            Stores = values.Stores
        };
    }

    private static LibraryFate Fate(string parameter, ClassifiedFate classified,
                                    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<LibraryValue>>> inputs) =>
        new(parameter, classified.Fate switch
        {
            FateClassifier.INVOKE_NOW => LibraryFateKind.InvokeNow,
            FateClassifier.ITERATOR => LibraryFateKind.Iterator,
            FateClassifier.HOLDER => LibraryFateKind.Holder,
            FateClassifier.UNKNOWN_EXECUTION => LibraryFateKind.UnknownExecution,
            FateClassifier.NOT_RUN => LibraryFateKind.NotRun,
            _ => throw new UnreachableException($"Unknown classified fate {classified.Fate}.")
        }, classified.Holder switch
        {
            FateClassifier.RESULT => LibraryHolderKind.Result,
            FateClassifier.THIS => LibraryHolderKind.This,
            null => null,
            _ => throw new UnreachableException($"Unknown holder {classified.Holder}.")
        }, inputs.GetValueOrDefault(parameter));

    private static bool HasLibraryState(EffectReader effects, ValueProvenance values)
        => effects.StateStores.Any(store => !values.IsInKeepingChain(store.Region));

    private static bool EnumerationReadIsNamed(string parameter, GeneratedEffect effect, IReadOnlyList<LibraryFate> fates,
                                               ValueProvenance values)
    {
        if (effect.Roots.Count == 0 || effect.NonEnumerationRoots.Count != 0 ||
            effect.Roots.Any(root => !effect.EnumerationRoots.Contains(root, StringComparer.Ordinal)))
            return false;
        return effect.Roots.All(root => root switch
        {
            DriverSynthesizer.CALL =>
                fates.Where(fate => fate.Kind == LibraryFateKind.InvokeNow).SelectMany(fate => fate.Inputs?.SelectMany(input => input) ?? [])
                     .Concat(values.Keeps.Values.SelectMany(value => value))
                     .Concat(values.Stores.Values.SelectMany(value => value))
                     .Concat(values.Outputs.Values.SelectMany(output => output.Values))
                     .Any(value => NamesElements(value, parameter)),
            DriverSynthesizer.ENUMERATE =>
                (values.Result?.Leaf.Kind == LibraryResultKind.Sequence && values.Result.Leaf.Values.Any(value => NamesElements(value, parameter))) ||
                fates.Where(fate => fate.Kind == LibraryFateKind.Iterator).SelectMany(fate => fate.Inputs?.SelectMany(input => input) ?? [])
                     .Any(value => NamesElements(value, parameter)),
            _ => false
        });
    }

    private static bool NamesElements(LibraryValue value, string parameter) => value switch
    {
        ElementsValue { Source: ArgumentValue argument } when argument.Parameter == parameter => true,
        ElementsValue elements => NamesElements(elements.Source, parameter),
        SequenceValue sequence => sequence.Values.Any(item => NamesElements(item, parameter)),
        GroupingValue grouping => NamesElements(grouping.Key, parameter) || NamesElements(grouping.Values, parameter),
        // A completion value is what a task completes with, never the elements of the argument that names the task.
        CompletionValue => false,
        _ => false
    };

    /// <summary>The built-in models without those whose assembly ranges name the implementation assembly: the built-in layer serves only
    /// calls that leave the decompiled assembly, so a member of it made <c>extern</c> stays an opaque call.</summary>
    /// <param name="assemblyName">The implementation assembly's name.</param>
    private static LibraryModels ModelsOutside(string assemblyName) =>
        new(LibraryModels.BuiltIn.Members.Where(model => model.Assemblies.All(range => range.AssemblyName != assemblyName)),
            LibraryModels.BuiltIn.ImmutableTypes.Where(type => type.Assemblies.All(range => range.AssemblyName != assemblyName)));

    /// <summary>What one generation has found so far.</summary>
    /// <param name="request">The member asked for.</param>
    private sealed class Facts(GenerationRequest request)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public GenerationRequest Request { get; } = request;
        public GenerationImplementation? Implementation { get; set; }
        public string? Framework { get; set; }
        public IReadOnlyList<string> MissingDependencies { get; set; } = [];
        public int ExternBodies { get; set; }
        public IReadOnlyList<string> DefaultedValues { get; set; } = [];
        public IReadOnlyDictionary<string, string> Refusals { get; set; } = new SortedDictionary<string, string>();
        public IReadOnlyList<string> SetupWidened { get; set; } = [];
        public int Seeds { get; set; }
        public IReadOnlyList<string> Unseeded { get; set; } = [];
        public IReadOnlyDictionary<string, IReadOnlyList<string>> HolderTriggers { get; set; } =
            new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        public int ReachedBodies { get; set; }

        public GenerationTrace Refused(string reason, string detail, Driver? driver = null, ScopeRun? run = null) =>
            new(Answer(null, reason, detail, null, null), driver, run);

        public GeneratedAnswer Answer(IReadOnlyDictionary<string, ClassifiedFate>? classified, string? reason, string detail, LibraryModel? model,
                                      string? modelReason) =>
            new(SCHEMA_VERSION, Request.MemberId, new GenerationAssembly(Request.AssemblyName, Request.Version, Request.PackageId), classified, reason,
                model, modelReason,
                new GenerationRecord(Implementation, Framework, MissingDependencies, ExternBodies, DefaultedValues, Refusals, SetupWidened, Seeds,
                                     Unseeded, HolderTriggers, ReachedBodies, Math.Round(_clock.Elapsed.TotalSeconds, 1)))
            {
                Detail = detail
            };
    }
}
