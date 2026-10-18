namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The stages of <see cref="ModelGenerator"/>'s generation of one member, in the order the code runs them. Each observes the
/// generation's token at its start and at each boundary of its repeated work (<see cref="GenerationChecks"/>).</summary>
internal enum GenerationStage
{
    /// <summary><see cref="ImplementationAssemblies.Resolve(string, string, string?, string?, CancellationToken)"/>: its nuspec reads,
    /// dependency rounds and the scan of the candidate references, one check for each.</summary>
    Resolve,

    /// <summary><see cref="LibraryCompilation.Compile(ImplementationAssembly, GenerationChecks)"/>: the decompilation, each type on its
    /// own, the extern rewrite rounds, the compilation, and the wait for a compilation another caller started.</summary>
    CompileLibrary,

    /// <summary><see cref="EngineClaims.FirstIn(Microsoft.CodeAnalysis.IAssemblySymbol, CancellationToken)"/>'s scan of the compiled
    /// library.</summary>
    EngineClaims,

    /// <summary><see cref="DriverSynthesizer"/>: the driver's text and its compilation.</summary>
    Synthesize,

    /// <summary>Each run of the analysis pipeline of the seeding loop.</summary>
    Analyze,

    /// <summary><see cref="FateClassifier.Classify(Driver, ConcurrencyHunter.Analysis.ScopeRun)"/>.</summary>
    Classify,

    /// <summary>The holder's confirmation run and <see cref="FateClassifier.Confirmed"/>.</summary>
    ConfirmHolder,

    /// <summary><see cref="EffectReader"/> and <see cref="ValueProvenance"/>.</summary>
    ReadEffects,

    /// <summary>The model entry built from what was read, and its final check.</summary>
    BuildModel
}

/// <summary>The one place a generation observes its token: every stage checks through <see cref="Check"/>, which first reports the
/// check to the callback a test passes, so a test can cancel at the first or the second check of any stage without timing anything
/// against the machine's speed.</summary>
/// <param name="cancellationToken">Cancels the generation.</param>
/// <param name="reported">Told the stage and the ordinal of each check within that stage, before the check is made; 1 is the check at the
/// stage's start, and the count goes on across the seeding loop's repeats. <c>null</c> in production.</param>
internal sealed class GenerationChecks(CancellationToken cancellationToken, Action<GenerationStage, int>? reported = null)
{
    private readonly int[] _ordinals = new int[Enum.GetValues<GenerationStage>().Length];

    /// <summary>The token the checks observe, for the calls that take one.</summary>
    public CancellationToken Token => cancellationToken;

    /// <summary>Reports a check of a stage to the callback, then makes it. A stage may be checked from several threads at once.</summary>
    /// <param name="stage">The stage that checks.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public void Check(GenerationStage stage)
    {
        reported?.Invoke(stage, Interlocked.Increment(ref _ordinals[(int)stage]));
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>The checks of one stage, which a helper of the generation built for that stage calls at each iteration of a loop no constant
/// bounds and at the entry of each recursion. The default value checks nothing: a helper built outside a generation, as the public
/// constructors build it, runs as it always ran.</summary>
/// <param name="Checks">The generation's checks, or <see langword="null"/> for none.</param>
/// <param name="Stage">The stage the helper's work belongs to.</param>
internal readonly record struct StageChecks(GenerationChecks? Checks, GenerationStage Stage)
{
    /// <summary>Makes the stage's check; none when the value is the default.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public void Check() => Checks?.Check(Stage);

    /// <summary>The elements of a sequence, each one after a check of the stage; the sequence itself when there are no checks.</summary>
    /// <typeparam name="T">The type of the elements.</typeparam>
    /// <param name="source">The sequence.</param>
    public IEnumerable<T> Each<T>(IEnumerable<T> source) => Checks is null ? source : Checking(source, Checks, Stage);

    private static IEnumerable<T> Checking<T>(IEnumerable<T> source, GenerationChecks checks, GenerationStage stage)
    {
        foreach (var item in source)
        {
            checks.Check(stage);
            yield return item;
        }
    }
}
