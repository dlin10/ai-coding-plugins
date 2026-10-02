using System.Diagnostics;
using ConcurrencyHunter.Analysis;
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
/// <param name="ReachedBodies">The bodies the reachable set reached, <c>0</c> before the pipeline ran.</param>
/// <param name="Seconds">The wall time of the whole generation, rounded to 0.1.</param>
public sealed record GenerationRecord(GenerationImplementation? Implementation, string? Framework, IReadOnlyList<string> MissingDependencies,
                                      int ExternBodies, IReadOnlyList<string> DefaultedValues, IReadOnlyDictionary<string, string> Refusals,
                                      IReadOnlyList<string> SetupWidened, int ReachedBodies, double Seconds);

/// <summary>The generator's answer for one member (G-6): the fate of each parameter it classifies, or the reason it classified none.</summary>
/// <param name="SchemaVersion">The answer's schema version, <c>1</c>.</param>
/// <param name="Member">The declaration id asked for.</param>
/// <param name="Assembly">The assembly as asked for.</param>
/// <param name="Classified">The fates by parameter name, or <c>null</c> when nothing was classified.</param>
/// <param name="Reason">A reason of <see cref="GenerationReasons"/> when nothing was classified, else <c>null</c>.</param>
/// <param name="Generation">What the generation used and saw.</param>
public sealed record GeneratedAnswer(int SchemaVersion, string Member, GenerationAssembly Assembly, IReadOnlyDictionary<string, ClassifiedFate>? Classified,
                                     string? Reason, GenerationRecord Generation)
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
}

/// <summary>The <b>Model generator</b> of SPEC TD-034b up to delegate fates: finds a member's implementation assembly, decompiles and
/// compiles it, synthesizes a driver, analyses both with the run's own pipeline and classifies the fates. It reads no project models,
/// writes no file and makes no network call.</summary>
public static class ModelGenerator
{
    private const int SCHEMA_VERSION = 1;
    private const int CLOSURE_BOUND = 1500;
    private const string SCOPE_ID = "model-generator";

    /// <summary>Whether a text is a declaration id the generator can be asked for: one the reader's grammar accepts (question 44),
    /// never the member pattern <c>(*)</c>. The grammar knows only <c>T:</c> and <c>M:</c>; a property's, an event's or a field's id
    /// is read as a member's and a namespace's as a type's, so those kinds reach the generator and get
    /// <c>driver-not-synthesized</c> (G-3) rather than a usage error.</summary>
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
    internal static GenerationTrace Trace(GenerationRequest request, LibraryCompilationResult library, CancellationToken cancellationToken) =>
        Classify(new Facts(request), library, cancellationToken);

    private static GenerationTrace Classify(Facts facts, LibraryCompilationResult library, CancellationToken cancellationToken)
    {
        facts.ExternBodies = library.ExternBodies;
        if (library.Compilation is not { } compilation)
            return facts.Refused(library.Reason ?? GenerationReasons.LIBRARY_DOES_NOT_COMPILE, string.Join(", ", library.Errors));
        if (DriverSynthesizer.FindMember(compilation, facts.Request.MemberId) is not { } member)
            return facts.Refused(GenerationReasons.MEMBER_NOT_FOUND, $"{compilation.AssemblyName} declares no {facts.Request.MemberId}");
        if (EngineClaims.FirstIn(compilation.Assembly) is { } claim)
            return facts.Refused(GenerationReasons.ENGINE_RECOGNIZED, $"{claim.Method} is claimed by the {claim.Recognizer} recognizer");

        var synthesis = DriverSynthesizer.Synthesize(compilation, member, library.ExternMembers, cancellationToken);
        if (synthesis.Driver is not { } driver)
            return facts.Refused(synthesis.Reason!, synthesis.Detail);
        facts.DefaultedValues = driver.DefaultedValues;

        var models = ModelsOutside(compilation.AssemblyName!);
        var lowered = new Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?>();
        ScopeRun Pipeline(bool triggers) =>
            ScopePipeline.Run(SCOPE_ID, [compilation, driver.Compilation], [], Path.GetTempPath(), new ProviderRegistry([new DriverRootProvider(triggers)]),
                              models, AnalysisLimits.Default, lowered, CLOSURE_BOUND, cancellationToken);

        ScopeRun run;
        try
        {
            run = Pipeline(triggers: false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return facts.Refused(GenerationReasons.ANALYSIS_FAILED, $"the engine threw {error.GetType().Name}: {error.Message}", driver);
        }

        facts.ReachedBodies = run.Reachable.ReachedBodies.Count;
        if (run.Stopped)
        {
            return facts.Refused(GenerationReasons.CLOSURE_BOUND, $"the reachable set reached {run.StoppedAtReachableBodies} bodies, over {CLOSURE_BOUND}",
                                 driver);
        }

        var body = IrLowering.RootBodyId(driver.Member);
        if (run.LoweringDiagnostics.FirstOrDefault(diagnostic => diagnostic.StartsWith($"lowering: {body}:", StringComparison.Ordinal)) is { } dropped)
            return facts.Refused(GenerationReasons.ANALYSIS_FAILED, dropped, driver, run);

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

        return new GenerationTrace(facts.Answer(classified, null, ""), driver, run) { Confirmation = confirmation };
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
        public int ReachedBodies { get; set; }

        public GenerationTrace Refused(string reason, string detail, Driver? driver = null, ScopeRun? run = null) =>
            new(Answer(null, reason, detail), driver, run);

        public GeneratedAnswer Answer(IReadOnlyDictionary<string, ClassifiedFate>? classified, string? reason, string detail) =>
            new(SCHEMA_VERSION, Request.MemberId, new GenerationAssembly(Request.AssemblyName, Request.Version, Request.PackageId), classified, reason,
                new GenerationRecord(Implementation, Framework, MissingDependencies, ExternBodies, DefaultedValues, Refusals, SetupWidened,
                                     ReachedBodies, Math.Round(_clock.Elapsed.TotalSeconds, 1)))
            {
                Detail = detail
            };
    }
}
