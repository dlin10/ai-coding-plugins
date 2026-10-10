using System.Runtime.CompilerServices;

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

    /// <summary>The member's own body did not compile, or is not the code the runtime runs (a member of <c>Unsafe</c>), and was made
    /// <c>extern</c>.</summary>
    public const string BODY_DOES_NOT_COMPILE = "body-does-not-compile";

    /// <summary>A recognizer of the engine claims a method of the implementation assembly by name, outside the models;
    /// for <c>System.Private.CoreLib</c> only, it must claim the requested member itself.</summary>
    public const string ENGINE_RECOGNIZED = "engine-recognized";

    /// <summary>The driver's reachable set reached more bodies than the generator's bound.</summary>
    public const string CLOSURE_BOUND = "closure-bound";

    /// <summary>The engine run threw, or its lowering dropped the member's own body.</summary>
    public const string ANALYSIS_FAILED = "analysis-failed";

    /// <summary>A probe or a value it reaches was touched where the analysis cannot see what happened.</summary>
    public const string UNKNOWN_TOUCH = "unknown-touch";

    /// <summary>The driver could not seed a field the member read.</summary>
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

    /// <summary>A field the member read could not be seeded.</summary>
    public const string INCOMPLETE = "incomplete";

    /// <summary>The member wrote pre-existing library state outside a keeping chain.</summary>
    public const string LIBRARY_STATE = "library-state";

    /// <summary>The observed effect or value has no form in the model vocabulary.</summary>
    public const string VOCABULARY = "vocabulary";

    /// <summary>The reasons in the order the generator checks them.</summary>
    public static IReadOnlyList<string> Ordered { get; } = [UNKNOWN_TOUCH, INCOMPLETE, LIBRARY_STATE, VOCABULARY];
}

/// <summary>One case the generation found against a model entry (SPEC TD-034b): the model reason it gives, the code that names the
/// case, and the first evidence of it in words.</summary>
/// <param name="Reason">The model reason of <see cref="ModelReasons"/> the case gives.</param>
/// <param name="Code">The case, one of <see cref="ModelCauses"/>.</param>
/// <param name="Detail">The first evidence in words: the value, field, parameter or call involved.</param>
public sealed record GenerationCause(string Reason, string Code, string Detail)
{
    /// <summary>The cause as the answer writes it: <c>code: detail</c>.</summary>
    public override string ToString() => $"{Code}: {Detail}";
}

/// <summary>The closed list of cases that give a model reason, by the code an answer names them with. Each case belongs to one
/// reason; a member's answer names every case found, while its <c>modelReason</c> is the first reason in check order.</summary>
public static class ModelCauses
{
    /// <summary><c>unknown-touch</c>: a probe, or a value reachable from one, was handed outside setup to a call the analysis does
    /// not follow.</summary>
    public const string PROBE_HANDED_TO_UNSEEN = "probe-handed-to-unseen";

    /// <summary><c>unknown-touch</c>: a probe, or a value reachable from one, was given outside setup to an operation the lowering does
    /// not express, and to no call the analysis does not follow. The lowering of that operation lifts the case.</summary>
    public const string PROBE_IN_UNSUPPORTED_OPERATION = "probe-in-unsupported-operation";

    /// <summary><c>unknown-touch</c>: a library body read or wrote a probe, or a value reachable from one, in an execution other than
    /// the member's own call and its children.</summary>
    public const string PROBE_ACCESSED_ELSEWHERE = "probe-accessed-elsewhere";

    /// <summary><c>unknown-touch</c>: outside setup the member called a member of an object an argument reaches that a witness stands
    /// for — code the analysis does not see, such as a <c>Dictionary</c>, a <c>MemoryStream</c> or a user class —, other than a getter,
    /// <c>ToString</c>, <c>Equals</c>, <c>GetHashCode</c> or a member of comparison, which it takes to read what they get.</summary>
    public const string ARGUMENT_MEMBER_UNSEEN = "argument-member-unseen";

    /// <summary><c>incomplete</c>: the member read a field setup could not seed or did not reach.</summary>
    public const string UNSEEDED_READ = "unseeded-read";

    /// <summary><c>library-state</c>: the member stored into a library object that may have existed before the call, outside any
    /// keeping chain. Looked for only when no <c>unknown-touch</c> or <c>incomplete</c> case is found, since it walks the keeping
    /// paths of every such store.</summary>
    public const string LIBRARY_STATE_STORE = "library-state-store";

    /// <summary><c>vocabulary</c>: what a witness returned was handed outside setup to a call the analysis does not follow.</summary>
    public const string WITNESS_VALUE_HANDED = "witness-value-handed";

    /// <summary><c>vocabulary</c>: a library body read or wrote what a witness returned in an execution other than the member's own
    /// call and its children.</summary>
    public const string WITNESS_VALUE_ACCESSED_ELSEWHERE = "witness-value-accessed-elsewhere";

    /// <summary><c>vocabulary</c>: the member read or wrote through a reference a witness returned.</summary>
    public const string WITNESS_REFERENCE = "witness-reference";

    /// <summary><c>vocabulary</c>: the member read a field of what a user delegate or a witness returned.</summary>
    public const string CALLBACK_VALUE_READ = "callback-value-read";

    /// <summary><c>vocabulary</c>: the member wrote a field of what a user delegate or a witness returned.</summary>
    public const string CALLBACK_VALUE_WRITTEN = "callback-value-written";

    /// <summary><c>vocabulary</c>: a known library call read or wrote what a user delegate or a witness returned.</summary>
    public const string CALLBACK_VALUE_IN_LIBRARY_CALL = "callback-value-in-library-call";

    /// <summary><c>vocabulary</c>: the member stored into a collection argument's own object, other than a cell of an array.</summary>
    public const string COLLECTION_ARGUMENT_STORE = "collection-argument-store";

    /// <summary><c>vocabulary</c>: the member wrote an object an argument reaches below its own object.</summary>
    public const string ARGUMENT_GRAPH_WRITE = "argument-graph-write";

    /// <summary><c>vocabulary</c>: the member wrote a cell of an array an argument reaches below its own object.</summary>
    public const string ARGUMENT_GRAPH_CELLS = "argument-graph-cells";

    /// <summary><c>vocabulary</c>: a field of a struct held in an object took a value that can hold a user object.</summary>
    public const string STRUCT_FIELD_OBJECT = "struct-field-object";

    /// <summary><c>vocabulary</c>: the member wrote the storage of a struct receiver or of a struct passed by <c>ref</c>, <c>out</c>
    /// or <c>in</c>.</summary>
    public const string STRUCT_STORAGE_WRITE = "struct-storage-write";

    /// <summary><c>vocabulary</c>: the member stored through a reference into a struct parameter passed by <c>ref</c>, <c>out</c> or
    /// <c>in</c>.</summary>
    public const string STRUCT_REFERENCE_WRITE = "struct-reference-write";

    /// <summary><c>vocabulary</c>: the member stored through a reference whose place is not proven.</summary>
    public const string UNPROVEN_REFERENCE_STORE = "unproven-reference-store";

    /// <summary><c>vocabulary</c>: a known library call that enumerates an argument also writes it.</summary>
    public const string ENUMERATION_WRITE = "enumeration-write";

    /// <summary><c>vocabulary</c>: an operation the lowering does not model assigns storage visible beyond a by-value local.</summary>
    public const string UNSUPPORTED_STORE = "unsupported-store";

    /// <summary><c>vocabulary</c>: a probe delegate carried inside a non-delegate argument ran in the member's call.</summary>
    public const string CARRIED_PROBE_FIRED = "carried-probe-fired";

    /// <summary><c>vocabulary</c>: the member returns a value the analysis resolves to no object.</summary>
    public const string RESULT_UNRESOLVED = "result-unresolved";

    /// <summary><c>vocabulary</c>: an output mixes named arguments with a form other than one of them.</summary>
    public const string OUTPUT_FORMS_MIXED = "output-forms-mixed";

    /// <summary><c>vocabulary</c>: a result or output is one of several objects, not all of which have a name.</summary>
    public const string RESULT_SEVERAL_OBJECTS = "result-several-objects";

    /// <summary><c>vocabulary</c>: a result or output is one object with no name, no collection, sequence or new form.</summary>
    public const string RESULT_UNNAMED = "result-unnamed";

    /// <summary><c>vocabulary</c>: a task argument's completion value keeps something, which <c>keeps</c> on the task cannot name.</summary>
    public const string COMPLETION_KEEPER = "completion-keeper";

    /// <summary><c>vocabulary</c>: a new result keeps the receiver.</summary>
    public const string RESULT_KEEPS_RECEIVER = "result-keeps-receiver";

    /// <summary><c>vocabulary</c>: a keeper keeps a user object no name reaches.</summary>
    public const string KEPT_USER_OBJECT = "kept-user-object";

    /// <summary><c>vocabulary</c>: a keeper keeps an object of unknown origin.</summary>
    public const string KEPT_UNKNOWN_OBJECT = "kept-unknown-object";

    /// <summary><c>vocabulary</c>: a value at a place the entry names may come from somewhere the analysis does not follow.</summary>
    public const string VALUE_UNOBSERVED = "value-unobserved";

    /// <summary><c>vocabulary</c>: a <c>ref</c> or <c>out</c> argument is assigned through a call the analysis has no parameter for.</summary>
    public const string OUTPUT_UNOBSERVED = "output-unobserved";

    /// <summary><c>vocabulary</c>: a value handed to a delegate may come from somewhere the analysis does not follow.</summary>
    public const string DELEGATE_INPUT_UNOBSERVED = "delegate-input-unobserved";

    /// <summary><c>vocabulary</c>: an object has more than one name.</summary>
    public const string VALUE_SEVERAL_NAMES = "value-several-names";

    /// <summary><c>vocabulary</c>: an object has no name and is not a fresh object the place may call <c>new</c>.</summary>
    public const string VALUE_UNNAMED = "value-unnamed";

    /// <summary><c>vocabulary</c>: a store through a reference into an array cell has no proven place or value.</summary>
    public const string REFERENCE_VALUE_UNOBSERVED = "reference-value-unobserved";

    /// <summary><c>vocabulary</c>: a read of the receiver reached a seed, a user object a program stored there.</summary>
    public const string RECEIVER_SEED_READ = "receiver-seed-read";

    /// <summary><c>vocabulary</c>: enumerating the result read an argument, and the result is no sequence that names it.</summary>
    public const string ENUMERATION_WITHOUT_SEQUENCE = "enumeration-without-sequence";

    /// <summary><c>vocabulary</c>: the project reader rejected the built entry.</summary>
    public const string ENTRY_REJECTED = "entry-rejected";
}

/// <summary>The causes one reader found: one per code, the first evidence of each kept.</summary>
internal sealed class GenerationCauses
{
    private readonly Dictionary<string, GenerationCause> _byCode = new(StringComparer.Ordinal);

    /// <summary>Records a case unless its code is already recorded.</summary>
    /// <param name="reason">The model reason the case gives.</param>
    /// <param name="code">The case, one of <see cref="ModelCauses"/>.</param>
    /// <param name="detail">The evidence in words.</param>
    public void Add(string reason, string code, string detail) => _byCode.TryAdd(code, new GenerationCause(reason, code, detail));

    /// <summary>Records a case unless its code is already recorded; the evidence is formatted, and its holes evaluated, only when it
    /// is kept, so a case met again in a loop costs one lookup.</summary>
    /// <param name="reason">The model reason the case gives.</param>
    /// <param name="code">The case, one of <see cref="ModelCauses"/>.</param>
    /// <param name="detail">The evidence in words, as an interpolated string.</param>
    public void Add(string reason, string code, [InterpolatedStringHandlerArgument("", "code")] ref CauseDetail detail)
    {
        if (detail.Kept)
            _byCode.TryAdd(code, new GenerationCause(reason, code, detail.ToStringAndClear()));
    }

    /// <summary>Whether a case of a code is already recorded.</summary>
    /// <param name="code">The case.</param>
    public bool Contains(string code) => _byCode.ContainsKey(code);

    /// <summary>Records every cause of another reader that is not already recorded.</summary>
    /// <param name="causes">The causes.</param>
    public void Add(IEnumerable<GenerationCause> causes)
    {
        foreach (var cause in causes)
            _byCode.TryAdd(cause.Code, cause);
    }

    /// <summary>Whether a case of a model reason is recorded.</summary>
    /// <param name="reason">The model reason.</param>
    public bool Has(string reason) => _byCode.Values.Any(cause => cause.Reason == reason);

    /// <summary>The first model reason in check order that a recorded case gives, or <c>null</c>.</summary>
    public string? Reason => ModelReasons.Ordered.FirstOrDefault(Has);

    /// <summary>The recorded causes, sorted by code.</summary>
    public IReadOnlyList<GenerationCause> All => _byCode.Values.OrderBy(cause => cause.Code, StringComparer.Ordinal).ToArray();
}

/// <summary>The evidence of a case as an interpolated string, formatted only when the case's code is not recorded yet.</summary>
[InterpolatedStringHandler]
internal ref struct CauseDetail
{
    private DefaultInterpolatedStringHandler _text;

    /// <summary>Starts the evidence of one case.</summary>
    /// <param name="literalLength">The length of the literal parts.</param>
    /// <param name="formattedCount">The number of holes.</param>
    /// <param name="causes">The causes the case is recorded into.</param>
    /// <param name="code">The case.</param>
    /// <param name="kept">Whether the evidence is formatted at all.</param>
    public CauseDetail(int literalLength, int formattedCount, GenerationCauses causes, string code, out bool kept)
    {
        kept = !causes.Contains(code);
        Kept = kept;
        _text = kept ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>Whether the evidence was formatted.</summary>
    public bool Kept { get; }

    /// <summary>Appends a literal part.</summary>
    /// <param name="value">The literal.</param>
    public void AppendLiteral(string value) => _text.AppendLiteral(value);

    /// <summary>Appends a hole.</summary>
    /// <typeparam name="T">The hole's type.</typeparam>
    /// <param name="value">The hole's value.</param>
    public void AppendFormatted<T>(T value) => _text.AppendFormatted(value);

    /// <summary>The evidence, releasing the buffer.</summary>
    public string ToStringAndClear() => _text.ToStringAndClear();
}
