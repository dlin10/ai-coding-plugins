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
/// <param name="SchemaVersion">The answer's schema version, <c>3</c>.</param>
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

    /// <summary>Every case the generation found against the model entry, one per code, sorted by code; empty when the member has a
    /// model or was not classified. <see cref="ModelReason"/> is the first reason in check order among them.</summary>
    public IReadOnlyList<GenerationCause> Causes { get; init; } = [];
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
    private const int SCHEMA_VERSION = 3;
    private const int CLOSURE_BOUND = 1500;
    private const string SCOPE_ID = "model-generator";
    private const string TRACING = "System.Diagnostics.Tracing";

    /// <summary>Whether a text is a declaration id the generator can be asked for: one the reader's grammar accepts (question 44),
    /// never the member pattern <c>(*)</c>. The grammar knows only <c>T:</c> and <c>M:</c>; a property's, an event's or a field's id
    /// is read as a member's and a namespace's as a type's, so those kinds reach the generator rather than a usage error: a
    /// <c>P:</c> or <c>E:</c> id is refused with <c>accessor</c> (G-6: after <c>member-not-found</c>, before
    /// <c>engine-recognized</c>), its detail naming the <c>M:</c> ids of the accessors, which are generated as methods are; an
    /// <c>F:</c> id is refused with <c>driver-not-synthesized</c>. An id that carries Roslyn's empty type name for a function-pointer
    /// type (<see cref="DeclarationId.HasEmptyTypeSegment"/>) reaches the generator too, and is refused with <c>unnamed-member</c>;
    /// the pattern <c>(*)</c> stays a usage error whatever it might stand for.</summary>
    /// <param name="id">The text.</param>
    public static bool IsMemberId(string? id)
    {
        if (Grammar(id) is not { } grammar)
            return false;
        var parsed = DeclarationId.Parse(grammar);
        return parsed is { IsPattern: false } || parsed is null && DeclarationId.HasEmptyTypeSegment(grammar);
    }

    /// <summary>An id in the form <see cref="DeclarationId.Parse"/> reads: the kinds the grammar does not know are read as the member
    /// or type kinds.</summary>
    /// <param name="id">The text.</param>
    private static string? Grammar(string? id)
    {
        if (id is not { Length: > 2 } || id[1] != ':')
            return null;
        return id[0] switch
        {
            'M' or 'P' or 'E' or 'F' => "M" + id[1..],
            'T' or 'N' => "T" + id[1..],
            _ => null
        };
    }

    /// <summary>The refusal for a member id whose text carries the empty type name of a function-pointer type, or <c>null</c>: no id names
    /// such a member, so nothing is resolved for it.</summary>
    /// <param name="facts">What the generation has found so far.</param>
    private static GenerationTrace? Unnamed(Facts facts) =>
        Grammar(facts.Request.MemberId) is { } grammar && DeclarationId.HasEmptyTypeSegment(grammar)
            ? facts.Refused(GenerationReasons.UNNAMED_MEMBER,
                            $"{facts.Request.MemberId} has the empty type name of a function-pointer type, which no declaration id names")
            : null;

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
    /// <param name="cancellationToken">Cancels the generation: every stage checks it (<see cref="GenerationStage"/>), and a cancelled
    /// generation throws <see cref="OperationCanceledException"/>.</param>
    /// <param name="checkReported">Told the stage and the ordinal of each check within that stage before the check is made, for tests;
    /// <c>null</c> in production.</param>
    internal static GenerationTrace Trace(GenerationRequest request, ImplementationAssemblies resolver, CancellationToken cancellationToken,
                                          Action<GenerationStage, int>? checkReported = null)
    {
        using var work = WorkCancellation.Enter(cancellationToken);
        GenerationChecks checks = new(WorkCancellation.Token, checkReported);
        checks.Check(GenerationStage.Resolve);
        var facts = new Facts(request);
        if (Unnamed(facts) is { } unnamed)
            return unnamed;
        var resolution = resolver.Resolve(request.AssemblyName, request.Version, request.PackageId, request.Framework, checks);
        // The platform is the one the resolver chose, also when it found no assembly there.
        facts.Framework = resolution.Framework;
        if (resolution.Assembly is { } opened)
        {
            facts.Implementation = new GenerationImplementation(opened.Path, opened.AssemblyVersion, opened.FileVersion, opened.Mvid.ToString());
            facts.MissingDependencies = opened.MissingDependencies;
        }

        if (resolution.Reason is { } reason)
            return facts.Refused(reason, resolution.Detail);
        var library = LibraryCompilation.Compile(resolution.Assembly!, checks);
        return Classify(facts, library, checks);
    }

    /// <summary>The answer for one member of a library already compiled, from the member check on: no implementation assembly, no
    /// framework.</summary>
    /// <param name="request">The member asked for.</param>
    /// <param name="library">The library compilation.</param>
    /// <param name="cancellationToken">Cancels the generation.</param>
    /// <param name="models">The models the runs use in place of the built-in ones outside the library, for tests of a dependency's
    /// model; <c>null</c> for those.</param>
    /// <param name="checkReported">Told the stage and the ordinal of each check within that stage before the check is made, for tests;
    /// <c>null</c> in production.</param>
    internal static GenerationTrace Trace(GenerationRequest request, LibraryCompilationResult library, CancellationToken cancellationToken,
                                          LibraryModels? models = null, Action<GenerationStage, int>? checkReported = null)
    {
        using var work = WorkCancellation.Enter(cancellationToken);
        var facts = new Facts(request);
        return Unnamed(facts) ?? Classify(facts, library, new GenerationChecks(WorkCancellation.Token, checkReported), models);
    }

    private static GenerationTrace Classify(Facts facts, LibraryCompilationResult library, GenerationChecks checks, LibraryModels? models = null)
    {
        WorkCancellation.Check();
        facts.ExternBodies = library.ExternBodies;
        if (library.Compilation is not { } compilation)
            return facts.Refused(library.Reason ?? GenerationReasons.LIBRARY_DOES_NOT_COMPILE, string.Join(", ", library.Errors));
        if (DriverSynthesizer.FindMember(compilation, facts.Request.MemberId) is not { } member)
            return facts.Refused(GenerationReasons.MEMBER_NOT_FOUND, $"{compilation.AssemblyName} declares no {facts.Request.MemberId}");
        if (member is IMethodSymbol signature && FunctionPointers.In(signature))
        {
            return facts.Refused(GenerationReasons.UNNAMED_MEMBER,
                                 $"{facts.Request.MemberId} has a function-pointer type in its signature, which no declaration id names");
        }
        if (DriverSynthesizer.IsAccessor(member))
            return facts.Refused(GenerationReasons.ACCESSOR, DriverSynthesizer.AccessorDetail(member));
        var corelib = compilation.AssemblyName == "System.Private.CoreLib";
        checks.Check(GenerationStage.EngineClaims);
        var claim = corelib
            ? member is IMethodSymbol method && EngineClaims.Of(method) is { } recognizer
                ? new EngineClaim(facts.Request.MemberId, recognizer) : null
            : EngineClaims.FirstIn(compilation.Assembly, checks);
        if (claim is not null)
            return facts.Refused(GenerationReasons.ENGINE_RECOGNIZED, $"{claim.Method} is claimed by the {claim.Recognizer} recognizer");

        var synthesis = DriverSynthesizer.Synthesize(compilation, member, library.ExternMembers, library.OpenedFields,
                                                     new HashSet<(string Type, string Name)>(), new ComparisonWitnesses(), checks,
                                                     library.ReplacedMembers);
        if (synthesis.Driver is not { } driver)
            return facts.Refused(synthesis.Reason!, synthesis.Detail);
        if (Overridable(driver.Member))
        {
            return facts.Refused(GenerationReasons.OVERRIDABLE,
                                 $"{facts.Request.MemberId} is {(driver.Member.IsOverride ? "an override not sealed" : "virtual")} in " +
                                 $"{driver.Member.ContainingType.ToDisplayString()}, which is not sealed: a call may run another type's override, " +
                                 "which the driver does not analyse");
        }
        facts.DefaultedValues = driver.DefaultedValues;
        facts.Seeds = driver.SeedStatements.Count;
        facts.Unseeded = driver.Unseeded;

        models ??= ModelsOutside(compilation.AssemblyName!);
        var loadedStatics = new HashSet<(string Type, string Name)>();
        var witnesses = new ComparisonWitnesses();
        var lowered = new Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?>();
        // The driver's probes, subclasses and seeds stand for the user's objects, of whatever type they are.
        var reachability = (corelib
            ? new ReachabilityRules(DispatchRule.ConstructedTypes, CoreLibLeftOutTypeKeys(compilation, driver.Member))
            {
                SilentTypeKeys = CoreLibSilentTypeKeys(compilation, driver.Member)
            }
            : ReachabilityRules.ClassHierarchy) with
        {
            OpenWorldAssemblies = new HashSet<string>(StringComparer.Ordinal) { DriverSynthesizer.ASSEMBLY },
            CalledPropertyKeys = driver.Member.AssociatedSymbol is IPropertySymbol property
                ? new HashSet<string>(StringComparer.Ordinal) { $"{property.ContainingAssembly.Name}:{property.OriginalDefinition.GetDocumentationCommentId()}" }
                : new HashSet<string>(StringComparer.Ordinal)
        };
        // A stage that runs the pipeline checks at its start and before each step the pipeline takes.
        ScopeRun Pipeline(bool triggers, GenerationStage stage)
        {
            checks.Check(stage);
            return ScopePipeline.Run(SCOPE_ID, [compilation, driver.Compilation], [], Path.GetTempPath(), new ProviderRegistry([new DriverRootProvider(triggers)]),
                                     models, AnalysisLimits.Default, lowered, CLOSURE_BOUND, checks.Token, _ => checks.Check(stage),
                                     reachability: reachability,
                                     fieldPropertySites: driver.SeedStatements.Select(seed => (seed.Operation.Syntax.SyntaxTree, seed.Operation.Syntax.Span)).ToArray());
        }

        ScopeRun run;
        // The lowering cache is shared by the runs, so a dropped body is reported only by the run that first lowered it.
        var dropped = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                WorkCancellation.Check();
                run = Pipeline(triggers: false, GenerationStage.Analyze);
                dropped.UnionWith(run.LoweringDiagnostics);
                run = run with { LoweringDiagnostics = dropped.ToArray() };
                var previousCount = loadedStatics.Count;
                // A probe whose closure reaches a default comparer implements its comparison interface on the next pass, a pass the
                // closure bound stopped included: the comparer CoreLib selects for a probe without the interface may be what passed it.
                var needsGrew = witnesses.Add(run, driver);
                // A pass the bound stopped loads what a closure past the bound reaches, and a pass whose probes the next one changes what
                // the comparer of a probe without the interface reaches, which the next pass's closure need not (question 142).
                if (!run.Stopped && !needsGrew)
                    loadedStatics.UnionWith(SeedableFields.LoadedStatics(run).Where(field =>
                        field.Type.StartsWith(compilation.AssemblyName + ":", StringComparison.Ordinal)));
                if (!needsGrew && (run.Stopped || loadedStatics.Count == previousCount))
                    break;
                synthesis = DriverSynthesizer.Synthesize(compilation, member, library.ExternMembers, library.OpenedFields, loadedStatics,
                                                         witnesses, checks, library.ReplacedMembers);
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
        // What the run shows is the member's only when the call ran its body: a receiver a factory made may dispatch to an override.
        if (!RanMember(driver, run, body))
            return facts.Refused(GenerationReasons.ANALYSIS_FAILED, $"the call never reached {body}", driver, run);

        FateClassification classification;
        try
        {
            classification = FateClassifier.Classify(driver, run, checks);
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
                    confirmation = Pipeline(triggers: true, GenerationStage.ConfirmHolder);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    confirmation = null;
                }
            }

            classified = FateClassifier.Confirmed(driver, classified, confirmation, checks);
        }

        checks.Check(GenerationStage.ReadEffects);
        var effects = new EffectReader(driver, run, classified, checks);
        var values = new ValueProvenance(driver, run, classified, confirmation, checks);
        facts.Unseeded = driver.Unseeded.Concat(effects.UnreachedSeeds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        facts.HolderTriggers = values.HolderTriggers;

        // The answer names every case found, and the model reason is the first in check order. The library-state check walks the
        // keeping paths of each state store, so it is looked for only while no earlier reason is found, and only to its first case.
        var causes = new GenerationCauses();
        causes.Add(effects.Causes);
        causes.Add(values.Causes);
        if (!causes.Has(ModelReasons.UNKNOWN_TOUCH) && !causes.Has(ModelReasons.INCOMPLETE) &&
            effects.StateStores.FirstOrDefault(store => !values.IsInKeepingChain(store.Region)) is { } state)
        {
            causes.Add(ModelReasons.LIBRARY_STATE, ModelCauses.LIBRARY_STATE_STORE, $"{state.Region} in {string.Join(" and ", state.Roots)}");
        }
        var built = BuildModel(driver.Member, classified, effects, values, causes, checks);
        var modelReason = causes.Reason;
        var detail = "";
        LibraryModel? model = null;
        if (modelReason is null)
        {
            // The entry is written with a check at each of its parts, and read and validated as a project file's is, which nothing
            // inside cancels: each step is bounded by the one entry, with a check between them and a last one after them.
            checks.Check(GenerationStage.BuildModel);
            var bytes = ModelEntryWriter.File(built, checks);
            checks.Check(GenerationStage.BuildModel);
            var files = ProjectModelFiles.Read("generated.json", bytes);
            checks.Check(GenerationStage.BuildModel);
            detail = files.Rejections.FirstOrDefault()?.Reason ??
                     (files.Entries.Count == 1 ? ProjectModelResolver.EntryRejection(files.Entries[0], driver.Member, compilation) :
                      $"the generated file contains {files.Entries.Count} entries") ?? "";
            if (detail.Length == 0)
                model = built;
            else
            {
                causes.Add(ModelReasons.VOCABULARY, ModelCauses.ENTRY_REJECTED, detail);
                modelReason = ModelReasons.VOCABULARY;
            }
        }

        // The last check: a cancellation that came while the entry was validated is not returned as an answer.
        checks.Check(GenerationStage.BuildModel);
        return new GenerationTrace(facts.Answer(classified, null, detail, model, modelReason, causes.All), driver, run)
        {
            Confirmation = confirmation,
            StateStores = effects.StateStores
        };
    }

    /// <summary>Whether a call may dispatch away from a member: it is virtual, or an override not sealed, in a type not sealed, so another
    /// type's override may run where a call names it, and a model is applied by the member a call names.</summary>
    /// <param name="member">The member.</param>
    private static bool Overridable(IMethodSymbol member) =>
        (member.IsVirtual || member.IsOverride && !member.IsSealed) && !member.ContainingType.IsSealed;

    /// <summary>The definition keys of the types whose bodies CoreLib's generation of a member never enters (ADR 0019): <c>System.SR</c>,
    /// each type a recognizer owns (<see cref="EngineClaims.RecognizedCoreLibTypeKeys"/>) but the one declaring the member, whose own
    /// body is what the run analyses, as a task awaiter's <c>OnCompleted</c>, which no recognizer claims, and the diagnostics the run
    /// takes to be off (<see cref="CoreLibSilentTypeKeys"/>).</summary>
    /// <param name="compilation">The compilation of CoreLib.</param>
    /// <param name="member">The member generated.</param>
    internal static IReadOnlySet<string> CoreLibLeftOutTypeKeys(Compilation compilation, IMethodSymbol member)
    {
        var keys = new HashSet<string>(EngineClaims.RecognizedCoreLibTypeKeys(compilation.Assembly), StringComparer.Ordinal)
        {
            "System.Private.CoreLib:System.SR"
        };
        keys.UnionWith(CoreLibSilentTypeKeys(compilation, member));
        keys.Remove(SymbolNames.TypeKey(member.ContainingType.OriginalDefinition));
        return keys;
    }

    /// <summary>The definition keys of the diagnostics CoreLib's generation of a member runs with off, as the runtime runs with no event
    /// listener and no stack trace taken (question 142): every type of <c>System.Diagnostics.Tracing</c>, every <c>EventSource</c> of
    /// another namespace, <c>StackTrace</c>, <c>StackFrame</c> and <c>StackFrameHelper</c>, with the types nested in them. A call, a creation
    /// and a field access of one of them does nothing (<see cref="ReachabilityRules.SilentTypeKeys"/>), so a member that traces, or formats
    /// an exception, reaches none of the manifests, resource strings and reflection they run. Empty when the member is one of theirs: what
    /// they do is what its generation analyses.</summary>
    /// <param name="compilation">The compilation of CoreLib.</param>
    /// <param name="member">The member generated.</param>
    internal static IReadOnlySet<string> CoreLibSilentTypeKeys(Compilation compilation, IMethodSymbol member) =>
        IsDiagnostics(member.ContainingType)
            ? new HashSet<string>(StringComparer.Ordinal)
            : EngineClaims.Types(compilation.Assembly.GlobalNamespace).Where(IsDiagnostics)
                          .Select(type => SymbolNames.TypeKey(type.OriginalDefinition)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a type is among the diagnostics CoreLib's generation runs with off (<see cref="CoreLibSilentTypeKeys"/>), as its
    /// outermost type is.</summary>
    /// <param name="type">The type.</param>
    private static bool IsDiagnostics(INamedTypeSymbol type)
    {
        while (type.ContainingType is { } containing)
        {
            WorkCancellation.Check();
            type = containing;
        }
        var space = type.ContainingNamespace.ToDisplayString();
        if (space == TRACING || (space == "System.Diagnostics" && type.Name is "StackTrace" or "StackFrame" or "StackFrameHelper"))
            return true;
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            WorkCancellation.Check();
            if (baseType.Name == "EventSource" && baseType.ContainingNamespace.ToDisplayString() == TRACING)
                return true;
        }

        return false;
    }

    /// <summary>Whether the member's own body ran in the tree of the driver's call or enumeration.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The completed run.</param>
    /// <param name="body">The member's body id.</param>
    private static bool RanMember(Driver driver, ScopeRun run, string body)
    {
        var executions = new DriverExecutions(driver, run.Executions!, new HashSet<string>(StringComparer.Ordinal));
        return run.Heap!.Instances.Values.Any(instance => instance.BodyId == body &&
            (run.Executions!.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>(StringComparer.Ordinal))
               .Any(execution => executions.InTree(execution, DriverSynthesizer.CALL) || executions.InTree(execution, DriverSynthesizer.ENUMERATE)));
    }

    /// <summary>The model entry the readers describe, without the effects the vocabulary cannot name; each of those is recorded as a
    /// cause, and an entry built with one is not the member's model.</summary>
    /// <param name="member">The generated member.</param>
    /// <param name="classified">The classified fates by parameter name.</param>
    /// <param name="reader">The effects read from the run.</param>
    /// <param name="values">The values named in the run.</param>
    /// <param name="causes">Where the cases the vocabulary cannot name are recorded.</param>
    /// <param name="checks">The generation's checks; the model checks at its start and before each effect it writes.</param>
    private static LibraryModel BuildModel(IMethodSymbol member, IReadOnlyDictionary<string, ClassifiedFate> classified, EffectReader reader,
                                           ValueProvenance values, GenerationCauses causes, GenerationChecks checks)
    {
        checks.Check(GenerationStage.BuildModel);
        var fates = member.Parameters.Where(parameter => TypeShape.Of(parameter.Type) == TypeShapeKind.Delegate &&
                                                        classified.ContainsKey(parameter.Name))
                          .Select(parameter => Fate(parameter.Name, classified[parameter.Name], values.Inputs))
                          .ToArray();
        var effects = new List<LibraryEffect>();
        foreach (var (parameter, observed) in reader.Effects)
        {
            WorkCancellation.Check();
            foreach (var effect in observed)
            {
                checks.Check(GenerationStage.BuildModel);
                // The receiver's own state is the library's: only what it holds of the user's — a seed, a probe its construction was
                // given — is an effect on this. A read of it is a deep read of this; a write has no form, since writes-arg on this would
                // name the receiver's own fields.
                if (parameter == FateClassifier.THIS && effect.Kind == EffectReader.WRITES_ARGUMENT)
                {
                    causes.Add(ModelReasons.VOCABULARY, ModelCauses.RECEIVER_OBJECT_WRITE, "writes-arg of this");
                    continue;
                }
                if (parameter == FateClassifier.THIS && effect.Kind == EffectReader.READS_DEEP && !effect.ReadsUserObject)
                    continue;
                if (effect.Kind == EffectReader.READS_DEEP && EnumerationReadIsNamed(parameter, effect, fates, values))
                    continue;
                if (effect.Roots.Contains(DriverSynthesizer.ENUMERATE, StringComparer.Ordinal) &&
                    values.Result?.Leaf.Kind != LibraryResultKind.Sequence && !IsCallsOwn(effect, values))
                {
                    causes.Add(ModelReasons.VOCABULARY, ModelCauses.ENUMERATION_WITHOUT_SEQUENCE, $"{effect.Kind} of {parameter}");
                    continue;
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
        return new LibraryModel(member.GetDocumentationCommentId()!,
                                [new SupportedAssemblyVersion(member.ContainingAssembly.Identity.Name, version, maximum)], effects)
        {
            Result = values.Result,
            Fates = fates,
            Keeps = values.Keeps,
            Outputs = values.Outputs,
            Stores = values.Stores,
            Calls = values.Calls
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

    /// <summary>Whether an effect the driver's enumeration made is the call's own: the result is named <c>collection(…)</c> or
    /// <c>dictionary(…)</c>, under any depth of <c>task(…)</c> — an array or an ADR 0010 collection, which the engine enumerates itself,
    /// so the driver's enumeration runs no code of the library beyond its own call of the member — and the member's call made the effect
    /// too. Such a result names no place that drops the read: <c>ToList</c> keeps <c>reads-deep</c> (TD-034b).</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="values">The values named in the run.</param>
    private static bool IsCallsOwn(GeneratedEffect effect, ValueProvenance values) =>
        values.Result?.Leaf.Kind is LibraryResultKind.Collection or LibraryResultKind.Dictionary &&
        effect.Roots.Contains(DriverSynthesizer.CALL, StringComparer.Ordinal);

    private static bool NamesElements(LibraryValue value, string parameter)
    {
        WorkCancellation.Check();
        return value switch
        {
            ElementsValue { Source: ArgumentValue argument } when argument.Parameter == parameter => true,
            ElementsValue elements => NamesElements(elements.Source, parameter),
            SequenceValue sequence => sequence.Values.Any(item => NamesElements(item, parameter)),
            GroupingValue grouping => NamesElements(grouping.Key, parameter) || NamesElements(grouping.Values, parameter),
            // A completion value is what a task completes with, never the elements of the argument that names the task.
            CompletionValue => false,
            _ => false
        };
    }

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
                                      string? modelReason, IReadOnlyList<GenerationCause>? causes = null) =>
            new(SCHEMA_VERSION, Request.MemberId, new GenerationAssembly(Request.AssemblyName, Request.Version, Request.PackageId), classified, reason,
                model, modelReason,
                new GenerationRecord(Implementation, Framework, MissingDependencies, ExternBodies, DefaultedValues, Refusals, SetupWidened, Seeds,
                                     Unseeded, HolderTriggers, ReachedBodies, Math.Round(_clock.Elapsed.TotalSeconds, 1)))
            {
                Detail = detail,
                Causes = model is null ? causes ?? [] : []
            };
    }
}
