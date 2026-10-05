namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The reasons the generator classifies nothing for a member (SPEC TD-034b), as its answer writes them.</summary>
public static class GenerationReasons
{
    /// <summary>A member of <c>System.Private.CoreLib</c>: its closure is the corelib itself.</summary>
    public const string CORELIB = "corelib";

    /// <summary>No implementation assembly of the version asked for is installed.</summary>
    public const string NO_IMPLEMENTATION = "no-implementation";

    /// <summary>The assembly found is a reference assembly, whose bodies are <c>throw null</c>.</summary>
    public const string REFERENCE_ASSEMBLY = "reference-assembly";

    /// <summary>The decompiled library has an error outside its bodies, or bodies still in error after the <c>extern</c> rewrite.</summary>
    public const string LIBRARY_DOES_NOT_COMPILE = "library-does-not-compile";

    /// <summary>The library declares no member of the declaration id asked for.</summary>
    public const string MEMBER_NOT_FOUND = "member-not-found";

    /// <summary>A property, event or accessor; generation for accessors belongs to run B2b.</summary>
    public const string ACCESSOR = "accessor";

    /// <summary>A member kind the generator does not take, or a member no driver could be built for.</summary>
    public const string DRIVER_NOT_SYNTHESIZED = "driver-not-synthesized";

    /// <summary>The member takes no delegate and no holding value that carries probes.</summary>
    public const string NOT_A_CANDIDATE = "not-a-candidate";

    /// <summary>The member's own body did not compile and was made <c>extern</c>.</summary>
    public const string BODY_DOES_NOT_COMPILE = "body-does-not-compile";

    /// <summary>A recognizer of the engine claims a method of the implementation assembly by name, outside the models.</summary>
    public const string ENGINE_RECOGNIZED = "engine-recognized";

    /// <summary>The driver's reachable set reached more bodies than the generator's bound.</summary>
    public const string CLOSURE_BOUND = "closure-bound";

    /// <summary>The engine run threw, or its lowering dropped the member's own body.</summary>
    public const string ANALYSIS_FAILED = "analysis-failed";

    /// <summary>A probe or a value it reaches was touched where the analysis cannot see what happened.</summary>
    public const string UNKNOWN_TOUCH = "unknown-touch";

    /// <summary>The driver could not seed a field the member read, or the member awaited an untracked completion value.</summary>
    public const string INCOMPLETE = "incomplete";

    /// <summary>The observed effect or value has no form in the library-model vocabulary.</summary>
    public const string VOCABULARY = "vocabulary";

    /// <summary>The reasons in the order the generator checks them (G-6); <c>driver-not-synthesized</c> stands twice, for a member kind
    /// the generator does not take and for a driver that could not be built.</summary>
    public static IReadOnlyList<string> Ordered { get; } =
    [
        CORELIB, NO_IMPLEMENTATION, REFERENCE_ASSEMBLY, LIBRARY_DOES_NOT_COMPILE, MEMBER_NOT_FOUND, ACCESSOR, ENGINE_RECOGNIZED, DRIVER_NOT_SYNTHESIZED,
        NOT_A_CANDIDATE, BODY_DOES_NOT_COMPILE, DRIVER_NOT_SYNTHESIZED, CLOSURE_BOUND, ANALYSIS_FAILED
    ];
}

/// <summary>The closed list of reasons a classified member has no generated model entry (SPEC TD-034b).</summary>
public static class ModelReasons
{
    /// <summary>A probe or reachable value was touched where the analysis cannot see what happened.</summary>
    public const string UNKNOWN_TOUCH = "unknown-touch";

    /// <summary>A field the member read could not be seeded, or an awaited completion value was not tracked.</summary>
    public const string INCOMPLETE = "incomplete";

    /// <summary>The member wrote pre-existing library state outside a keeping chain.</summary>
    public const string LIBRARY_STATE = "library-state";

    /// <summary>The observed effect or value has no form in the model vocabulary.</summary>
    public const string VOCABULARY = "vocabulary";

    /// <summary>The reasons in the order the generator checks them.</summary>
    public static IReadOnlyList<string> Ordered { get; } = [UNKNOWN_TOUCH, INCOMPLETE, LIBRARY_STATE, VOCABULARY];
}
