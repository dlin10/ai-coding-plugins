using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ConcurrencyHunter.Providers.LibraryModels;

/// <summary>Where a delegate a known call receives runs (ADR 0012): at the call, where the library sequence it makes is enumerated,
/// where a member of its holder is called, in startup, in an unknown execution, or nowhere: the call neither runs nor keeps it
/// (ADR 0015).</summary>
public enum LibraryFateKind { InvokeNow, Iterator, Holder, Startup, UnknownExecution, NotRun }

/// <summary>The object that keeps a <see cref="LibraryFateKind.Holder"/> delegate: what the call returns, or its receiver.</summary>
public enum LibraryHolderKind { Result, This }

/// <summary>The fate of one delegate parameter, with what each parameter of the delegate's <c>Invoke</c> is handed. Null
/// <see cref="Inputs"/> hands every parameter nothing known, as an empty set for each does.</summary>
/// <param name="Parameter">The delegate parameter's name.</param>
/// <param name="Kind">Where the delegate runs.</param>
/// <param name="Holder">The holder of a holder fate, otherwise null.</param>
/// <param name="Inputs">The values handed to each delegate parameter, or null for no known inputs.</param>
public sealed record LibraryFate(string Parameter, LibraryFateKind Kind, LibraryHolderKind? Holder,
                                 IReadOnlyList<IReadOnlyList<LibraryValue>>? Inputs)
{
    internal static string Text(LibraryFateKind kind) => kind switch
    {
        LibraryFateKind.InvokeNow => "invoke-now",
        LibraryFateKind.Iterator => "iterator",
        LibraryFateKind.Holder => "holder",
        LibraryFateKind.Startup => "startup",
        LibraryFateKind.UnknownExecution => "unknown-execution",
        LibraryFateKind.NotRun => "not-run",
        _ => throw new UnreachableException($"Unknown fate kind {kind}.")
    };
}

/// <summary>A value of the model format's grammar: a set of objects a delegate is handed or a call returns.</summary>
public abstract record LibraryValue
{
    /// <summary>The value <paramref name="text"/> writes, or null when it breaks the grammar anywhere.</summary>
    public static LibraryValue? Parse(string text) => LibraryVocabulary.ValueParser.Whole(text, parser => parser.Value());

    /// <summary>The text two values that mean the same share, whatever order a <c>sequence</c> lists its values in.</summary>
    internal abstract string Canonical { get; }
}

/// <summary><c>new</c>: a fresh object of a delegate parameter's type.</summary>
public sealed record NewValue : LibraryValue
{
    public override string ToString() => "new";
    internal override string Canonical => ToString();
}

/// <summary><c>arg:P</c>: what the argument of parameter P points to.</summary>
public sealed record ArgumentValue(string Parameter) : LibraryValue
{
    public override string ToString() => "arg:" + Parameter;
    internal override string Canonical => ToString();
}

/// <summary><c>this</c>: the receiver's objects, or the object a constructor creates.</summary>
public sealed record ThisValue : LibraryValue
{
    public override string ToString() => "this";
    internal override string Canonical => ToString();
}

/// <summary><c>returns:D</c>: every object any run of delegate D returns.</summary>
public sealed record ReturnsValue(string Delegate) : LibraryValue
{
    public override string ToString() => "returns:" + Delegate;
    internal override string Canonical => ToString();
}

/// <summary><c>holder-arg:N</c>: the N-th argument of the call of a holder's member.</summary>
public sealed record HolderArgumentValue(int Index) : LibraryValue
{
    public override string ToString() => "holder-arg:" + Index;
    internal override string Canonical => ToString();
}

/// <summary><c>elements(v)</c>: what enumerating v yields.</summary>
public sealed record ElementsValue(LibraryValue Source) : LibraryValue
{
    public override string ToString() => $"elements({Source})";
    internal override string Canonical => $"elements({Source.Canonical})";
}

/// <summary><c>sequence(v, …)</c>: a new library sequence yielding the values.</summary>
public sealed record SequenceValue(IReadOnlyList<LibraryValue> Values) : LibraryValue
{
    public override string ToString() => $"sequence({string.Join(',', Values)})";
    internal override string Canonical => $"sequence({LibraryResult.Set(Values)})";
}

/// <summary><c>grouping(k, v)</c>: a new object whose <c>Key</c> is k and which yields v.</summary>
public sealed record GroupingValue(LibraryValue Key, LibraryValue Values) : LibraryValue
{
    public override string ToString() => $"grouping({Key},{Values})";
    internal override string Canonical => $"grouping({Key.Canonical},{Values.Canonical})";
}

/// <summary>Everything kept by the named receiver or parameter.</summary>
/// <param name="Keeper">The receiver or parameter name.</param>
public sealed record KeptValue(string Keeper) : LibraryValue
{
    public override string ToString() => $"kept:{Keeper}";
    internal override string Canonical => ToString();
}

public enum LibraryResultKind { Sequence, Collection, Dictionary, OneOf, New }

/// <summary>What a known call returns: a new library sequence, a new collection or array, a new dictionary (keys, then values), or
/// one of the objects the values name, or a new object graph of the destination's type.</summary>
/// <param name="Kind">The result form.</param>
/// <param name="Values">The values the form names.</param>
public sealed record LibraryResult(LibraryResultKind Kind, IReadOnlyList<LibraryValue> Values)
{
    /// <summary>The result <paramref name="text"/> writes, or null when it breaks the grammar anywhere.</summary>
    /// <param name="text">The complete result form.</param>
    public static LibraryResult? Parse(string text) => LibraryVocabulary.ValueParser.Whole(text, parser => parser.Result());

    public override string ToString() => Kind switch
    {
        LibraryResultKind.Sequence => $"sequence({string.Join(',', Values)})",
        LibraryResultKind.Collection => $"collection({string.Join(',', Values)})",
        LibraryResultKind.Dictionary => $"dictionary({string.Join(',', Values)})",
        LibraryResultKind.OneOf => $"[{string.Join(',', Values)}]",
        LibraryResultKind.New => "new",
        _ => throw new UnreachableException($"Unknown result kind {Kind}.")
    };

    /// <summary>The text two results that mean the same share: a dictionary's keys and values apart, any other result's values as
    /// a set.</summary>
    internal string Canonical => Kind switch
    {
        LibraryResultKind.Dictionary => $"dictionary({string.Join(',', Values.Select(value => value.Canonical))})",
        LibraryResultKind.Sequence or LibraryResultKind.Collection or LibraryResultKind.OneOf => $"{Kind}({Set(Values)})",
        LibraryResultKind.New => "new",
        _ => throw new UnreachableException($"Unknown result kind {Kind}.")
    };

    internal static string Set(IEnumerable<LibraryValue> values) =>
        string.Join(',', values.Select(value => value.Canonical).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}

/// <summary>A fate as a model file writes it, before the entry step reads it.</summary>
internal sealed record RawFate(string Parameter, string? Fate, string? Holder, IReadOnlyList<IReadOnlyList<string>>? Inputs);

/// <summary>The rules of the model file format (ADR 0012): the entry step, which needs only the file, and the member step, which
/// needs the member's symbol. Each refusal is a <see cref="FormatException"/> or a reason naming what breaks the rule.</summary>
internal static class LibraryVocabulary
{
    // The collections of ADR 0010, by metadata name: what a collection(…) result may create besides an array.
    private static readonly HashSet<string> Collections = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Concurrent.ConcurrentStack`1",
        "System.Collections.Concurrent.ConcurrentBag`1",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.Queue`1",
        "System.Collections.Generic.Stack`1",
        "System.Collections.Generic.LinkedList`1"
    };

    /// <summary>The entry step: every string parses, <c>returns:D</c> names a fate whose runs are there to be had where it stands,
    /// <c>holder-arg:N</c> stands alone in a holder's inputs, an iterator fate has a <c>sequence</c> result, and a holder of the
    /// result goes without one.</summary>
    /// <param name="resultText">The result text, or null when absent.</param>
    /// <param name="raw">The fates as the file writes them.</param>
    /// <param name="effects">The effects that constrain store targets.</param>
    /// <param name="rawStores">The values written into each target's cells.</param>
    /// <param name="rawOutputs">The result forms assigned to output parameters.</param>
    /// <param name="rawKeeps">The values kept by each keeper.</param>
    internal static (LibraryResult? Result, IReadOnlyList<LibraryFate> Fates, IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> Stores,
                     IReadOnlyDictionary<string, LibraryResult> Outputs, IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> Keeps) Entry(
        string? resultText, IReadOnlyList<RawFate> raw, IReadOnlyList<LibraryEffect> effects, IReadOnlyDictionary<string, string[]>? rawStores,
        IReadOnlyDictionary<string, string>? rawOutputs = null, IReadOnlyDictionary<string, string[]>? rawKeeps = null)
    {
        LibraryResult? result = null;
        if (resultText is not null && (result = LibraryResult.Parse(resultText)) is null)
            throw new FormatException($"result '{resultText}' does not parse.");
        var fates = new Dictionary<string, LibraryFate>(StringComparer.Ordinal);
        foreach (var fate in raw)
        {
            // A key is a parameter name of the grammar, as `arg:` and `returns:` name one (review F-0073).
            if (ValueParser.Whole(fate.Parameter, parser => parser.Name()) is null)
                throw new FormatException($"fates must be keyed by parameter names; '{fate.Parameter}' is none.");
            var kind = fate.Fate switch
            {
                "invoke-now" => LibraryFateKind.InvokeNow,
                "iterator" => LibraryFateKind.Iterator,
                "holder" => LibraryFateKind.Holder,
                "startup" => LibraryFateKind.Startup,
                "unknown-execution" => LibraryFateKind.UnknownExecution,
                "not-run" => LibraryFateKind.NotRun,
                "di-factory" => throw new FormatException($"fate of '{fate.Parameter}': di-factory is not supported before phase 5d, which brings the generator."),
                null => throw new FormatException($"fate of '{fate.Parameter}' needs a fate."),
                _ => throw new FormatException($"fate of '{fate.Parameter}': unknown fate '{fate.Fate}'.")
            };
            LibraryHolderKind? holder = (kind, fate.Holder) switch
            {
                (LibraryFateKind.Holder, "result") => LibraryHolderKind.Result,
                (LibraryFateKind.Holder, "this") => LibraryHolderKind.This,
                (LibraryFateKind.Holder, null) => throw new FormatException($"fate of '{fate.Parameter}': a holder fate needs its holder."),
                (LibraryFateKind.Holder, _) => throw new FormatException($"fate of '{fate.Parameter}': holder must be result or this."),
                (_, null) => null,
                _ => throw new FormatException($"fate of '{fate.Parameter}': holder goes only with the holder fate.")
            };
            if (kind == LibraryFateKind.NotRun && fate.Inputs is not null)
                throw new FormatException($"fate of '{fate.Parameter}': not-run carries no inputs.");
            var inputs = fate.Inputs?.Select(input => (IReadOnlyList<LibraryValue>)input.Select(text =>
                             LibraryValue.Parse(text) ?? throw new FormatException($"fate of '{fate.Parameter}': input '{text}' does not parse."))
                         .ToArray()).ToArray();
            if (!fates.TryAdd(fate.Parameter, new LibraryFate(fate.Parameter, kind, holder, inputs)))
                throw new FormatException($"fates name '{fate.Parameter}' twice.");
        }

        foreach (var effect in effects)
        {
            if (effect.Parameter == "this" && effect.Kind != LibraryEffectKind.WriteCells)
                throw new FormatException("this is a target only of writes-cells.");
        }
        var stores = new Dictionary<string, IReadOnlyList<LibraryValue>>(StringComparer.Ordinal);
        foreach (var (target, texts) in rawStores ?? new Dictionary<string, string[]>())
        {
            if (target != "this" && ValueParser.Whole(target, parser => parser.Name()) is null || texts.Length == 0)
                throw new FormatException("stores must map a target to a non-empty array of values.");
            if (!effects.Any(effect => effect.Parameter == target && effect.Kind == LibraryEffectKind.WriteCells))
                throw new FormatException($"stores target '{target}' needs writes-cells.");
            var values = texts.Select(text => LibraryValue.Parse(text) ?? throw new FormatException($"stores value '{text}' does not parse.")).ToArray();
            foreach (var value in values)
                Place(value, fates, into: null, result: null, "stores", whole: true);
            stores.Add(target, values);
        }
        var outputs = new Dictionary<string, LibraryResult>(StringComparer.Ordinal);
        foreach (var (target, text) in rawOutputs ?? new Dictionary<string, string>())
        {
            if (ValueParser.Whole(target, parser => parser.Name()) is null)
                throw new FormatException($"outputs target '{target}' must be a parameter name.");
            var output = LibraryResult.Parse(text) ?? throw new FormatException($"outputs value '{text}' does not parse.");
            foreach (var value in output.Values)
                Place(value, fates, into: null, output, "outputs", whole: true);
            outputs.Add(target, output);
        }
        var keeps = new Dictionary<string, IReadOnlyList<LibraryValue>>(StringComparer.Ordinal);
        foreach (var (keeper, texts) in rawKeeps ?? new Dictionary<string, string[]>())
        {
            if (ValueParser.Whole(keeper, parser => parser.Name()) is null || texts.Length == 0)
                throw new FormatException("keeps must map a keeper to a non-empty array of values.");
            if (keeper == "result" && result?.Kind != LibraryResultKind.New &&
                !fates.Values.Any(fate => fate.Holder == LibraryHolderKind.Result))
                throw new FormatException("keeps.result needs the result new.");
            var values = texts.Select(text => LibraryValue.Parse(text) ?? throw new FormatException($"keeps value '{text}' does not parse.")).ToArray();
            foreach (var value in values)
                Place(value, fates, into: null, result: null, "keeps", whole: true);
            keeps.Add(keeper, values);
        }
        foreach (var fate in fates.Values)
            foreach (var value in fate.Inputs?.SelectMany(input => input) ?? [])
                Place(value, fates, fate, result: null, "input", whole: true);
        foreach (var value in result?.Values ?? [])
            Place(value, fates, into: null, result, result!.Kind switch
            {
                LibraryResultKind.Sequence => "sequence(…)",
                LibraryResultKind.Collection => "collection(…)",
                LibraryResultKind.Dictionary => "dictionary(…)",
                LibraryResultKind.OneOf => "result list",
                _ => "result"
            }, whole: true);
        if (fates.Values.Any(fate => fate.Kind == LibraryFateKind.Iterator) && result?.Kind != LibraryResultKind.Sequence)
            throw new FormatException("an iterator fate needs a sequence(…) result.");
        if (result is not null && fates.Values.Any(fate => fate.Holder == LibraryHolderKind.Result))
            throw new FormatException("an entry whose holder is the result carries no result.");
        return (result, fates.Values.ToArray(), stores, outputs, keeps);
    }

    /// <summary>Checks where a value stands: in the inputs of <paramref name="into"/>, or in <paramref name="result"/>.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="fates">The entry's fates.</param>
    /// <param name="into">The destination fate, if any.</param>
    /// <param name="result">The destination result, if any.</param>
    /// <param name="place">The model place containing the value.</param>
    /// <param name="whole">Whether the value stands outside another value.</param>
    private static void Place(LibraryValue value, IReadOnlyDictionary<string, LibraryFate> fates, LibraryFate? into, LibraryResult? result,
                              string place, bool whole)
    {
        switch (value)
        {
            case HolderArgumentValue when into?.Kind != LibraryFateKind.Holder || !whole:
                throw new FormatException($"{value} stands only alone in the inputs of a holder fate.");
            case NewValue when into is null || !whole:
                throw new FormatException($"new is not allowed in {place}.");
            case NewValue when into.Kind is not (LibraryFateKind.InvokeNow or LibraryFateKind.Holder):
                throw new FormatException("new input needs an invoke-now or holder fate.");
            case KeptValue { Keeper: "result" }:
                throw new FormatException("kept:result is not allowed.");
            case NewValue:
            case KeptValue:
            case HolderArgumentValue:
            case ArgumentValue:
            case ThisValue:
                break;
            case ReturnsValue returns:
                if (!fates.TryGetValue(returns.Delegate, out var source))
                    throw new FormatException($"{value} names no fate of the entry.");
                var available = source.Kind switch
                {
                    LibraryFateKind.InvokeNow => true,
                    LibraryFateKind.Iterator => result?.Kind == LibraryResultKind.Sequence ||
                                                into is { Kind: LibraryFateKind.Iterator } && into.Parameter != source.Parameter,
                    LibraryFateKind.Holder or LibraryFateKind.Startup or LibraryFateKind.UnknownExecution or LibraryFateKind.NotRun => false,
                    _ => throw new UnreachableException($"Unknown fate kind {source.Kind}.")
                };
                if (!available)
                    throw new FormatException($"{value}: the runs of a {LibraryFate.Text(source.Kind)} delegate are not there to be had here.");
                break;
            case ElementsValue elements:
                Place(elements.Source, fates, into, result, "elements(…)", whole: false);
                break;
            case SequenceValue sequence:
                foreach (var item in sequence.Values)
                    Place(item, fates, into, result, "sequence(…)", whole: false);
                break;
            case GroupingValue grouping:
                Place(grouping.Key, fates, into, result, "grouping(…)", whole: false);
                Place(grouping.Values, fates, into, result, "grouping(…)", whole: false);
                break;
            default:
                throw new UnreachableException($"Unknown value kind {value.GetType().Name}.");
        }
    }

    /// <summary>The member step on the member's original definition, or null when the entry fits it.</summary>
    /// <param name="result">The entry's result.</param>
    /// <param name="fates">The entry's fates.</param>
    /// <param name="method">The member's original definition.</param>
    /// <param name="compilation">The compilation that checks conversions.</param>
    /// <param name="effects">The entry's effects.</param>
    /// <param name="stores">The entry's stores.</param>
    /// <param name="outputs">The entry's output assignments.</param>
    /// <param name="keeps">The entry's keepers and kept values.</param>
    internal static string? Member(LibraryResult? result, IReadOnlyList<LibraryFate> fates, IMethodSymbol method, Compilation compilation,
                                   IReadOnlyList<LibraryEffect>? effects = null, IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>>? stores = null,
                                   IReadOnlyDictionary<string, LibraryResult>? outputs = null, IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>>? keeps = null)
    {
        try
        {
            new MemberCheck(result, fates, method, compilation, effects ?? [], stores ?? new Dictionary<string, IReadOnlyList<LibraryValue>>(),
                            outputs ?? new Dictionary<string, LibraryResult>(), keeps ?? new Dictionary<string, IReadOnlyList<LibraryValue>>()).Run();
            return null;
        }
        catch (Refusal refusal)
        {
            return refusal.Message;
        }
    }

    /// <summary>Whether two entries' results and fates say the same, whatever order anything is written in; absent inputs are an
    /// empty set for every parameter.</summary>
    /// <param name="resultA">The first entry's result.</param>
    /// <param name="fatesA">The first entry's fates.</param>
    /// <param name="resultB">The second entry's result.</param>
    /// <param name="fatesB">The second entry's fates.</param>
    /// <param name="storesA">The first entry's stores.</param>
    /// <param name="storesB">The second entry's stores.</param>
    /// <param name="outputsA">The first entry's outputs.</param>
    /// <param name="outputsB">The second entry's outputs.</param>
    /// <param name="keepsA">The first entry's keeps.</param>
    /// <param name="keepsB">The second entry's keeps.</param>
    internal static bool Alike(LibraryResult? resultA, IReadOnlyList<LibraryFate> fatesA, LibraryResult? resultB, IReadOnlyList<LibraryFate> fatesB,
                              IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> storesA, IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> storesB,
                              IReadOnlyDictionary<string, LibraryResult> outputsA, IReadOnlyDictionary<string, LibraryResult> outputsB,
                              IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>>? keepsA = null, IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>>? keepsB = null)
    {
        if (resultA?.Canonical != resultB?.Canonical || fatesA.Count != fatesB.Count || outputsA.Count != outputsB.Count ||
            outputsA.Any(output => !outputsB.TryGetValue(output.Key, out var other) || output.Value.Canonical != other.Canonical))
            return false;
        foreach (var a in fatesA)
        {
            if (fatesB.FirstOrDefault(fate => fate.Parameter == a.Parameter) is not { } b || a.Kind != b.Kind || a.Holder != b.Holder)
                return false;
            var count = Math.Max(a.Inputs?.Count ?? 0, b.Inputs?.Count ?? 0);
            for (var index = 0; index < count; index++)
            {
                if (InputSet(a, index) != InputSet(b, index))
                    return false;
            }
        }
        return SameValues(keepsA ?? new Dictionary<string, IReadOnlyList<LibraryValue>>(), keepsB ?? new Dictionary<string, IReadOnlyList<LibraryValue>>()) &&
               SameValues(storesA, storesB);
    }

    private static bool SameValues(IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> a, IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var values) && LibraryResult.Set(pair.Value) == LibraryResult.Set(values));

    private static string InputSet(LibraryFate fate, int index) =>
        fate.Inputs is { } inputs && index < inputs.Count ? LibraryResult.Set(inputs[index]) : "";

    private static bool IsDelegate(ITypeSymbol type) => type.TypeKind == TypeKind.Delegate;

    private sealed class Refusal(string reason) : Exception(reason);

    private sealed class MemberCheck(LibraryResult? result, IReadOnlyList<LibraryFate> fates, IMethodSymbol method, Compilation compilation,
                                    IReadOnlyList<LibraryEffect> effects, IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> stores,
                                    IReadOnlyDictionary<string, LibraryResult> outputs, IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> keeps)
    {
        internal void Run()
        {
            if (result is not null && fates.Any(fate => fate.Holder == LibraryHolderKind.Result))
                throw new Refusal("an entry whose holder is the result carries no result.");
            if (keeps.ContainsKey("result") && result?.Kind != LibraryResultKind.New &&
                !fates.Any(fate => fate.Holder == LibraryHolderKind.Result))
                throw new Refusal("keeps.result needs the result new.");
            foreach (var effect in effects)
            {
                var target = TargetType(effect.Parameter);
                switch (effect.Kind)
                {
                    case LibraryEffectKind.WriteCells:
                        if (effect.Parameter == "this" ? target.SpecialType != SpecialType.System_Array : target is not IArrayTypeSymbol && target.SpecialType != SpecialType.System_Array)
                            throw new Refusal(effect.Parameter == "this" ? "writes-cells on this needs an instance member of System.Array." :
                                              $"writes-cells target '{effect.Parameter}' must be an array or System.Array.");
                        break;
                    case LibraryEffectKind.DeepRead:
                    case LibraryEffectKind.WriteArgument:
                        break;
                    default:
                        throw new UnreachableException($"Unknown effect kind {effect.Kind}.");
                }
            }
            foreach (var store in stores)
            {
                var target = TargetType(store.Key);
                ConvertAll(store.Value, target is IArrayTypeSymbol arrayType ? arrayType.ElementType : compilation.GetSpecialType(SpecialType.System_Object));
            }
            if (method.Parameters.FirstOrDefault(parameter => parameter.Type is IArrayTypeSymbol { ElementType.TypeKind: TypeKind.Delegate }) is { } array)
                throw new Refusal($"parameter '{array.Name}' is an array of delegates, which no model can describe.");
            if (method.Parameters.FirstOrDefault(parameter => IsDelegate(parameter.Type) && fates.All(fate => fate.Parameter != parameter.Name)) is { } unfated)
                throw new Refusal($"delegate-typed parameter '{unfated.Name}' has no fate.");
            foreach (var fate in fates)
            {
                var parameter = method.Parameters.FirstOrDefault(candidate => candidate.Name == fate.Parameter) ??
                                throw new Refusal($"fate names '{fate.Parameter}', which is no parameter.");
                if (!IsDelegate(parameter.Type))
                    throw new Refusal($"fate names '{fate.Parameter}', which is not delegate-typed.");
            }
            foreach (var fate in fates)
            {
                var invoke = ((INamedTypeSymbol)method.Parameters.First(candidate => candidate.Name == fate.Parameter).Type).DelegateInvokeMethod!;
                if (fate.Inputs is { } inputs && inputs.Count != invoke.Parameters.Length)
                    throw new Refusal($"inputs of '{fate.Parameter}' name {inputs.Count} parameters; its delegate takes {invoke.Parameters.Length}.");
                if (fate.Holder == LibraryHolderKind.This && (method.IsStatic || method.MethodKind == MethodKind.Constructor))
                    throw new Refusal("holder this needs an instance method that is not a constructor.");
                if (fate.Holder == LibraryHolderKind.Result && method.MethodKind != MethodKind.Constructor && !method.ReturnType.IsReferenceType)
                    throw new Refusal("holder result needs a constructor or a method returning a reference type.");
                for (var index = 0; index < (fate.Inputs?.Count ?? 0); index++)
                {
                    foreach (var value in fate.Inputs![index])
                        if (value is NewValue)
                        {
                            if (fate.Kind is not (LibraryFateKind.InvokeNow or LibraryFateKind.Holder))
                                throw new Refusal("new input needs an invoke-now or holder fate.");
                            CheckNewInputType(invoke.Parameters[index].Type);
                        }
                        else if (value is not HolderArgumentValue)
                            Converts(value, invoke.Parameters[index].Type);
                }
            }
            foreach (var keep in keeps)
            {
                if (keep.Key != "result")
                    _ = TargetType(keep.Key);
                foreach (var value in keep.Value)
                    CheckLeaves(value);
            }
            foreach (var output in outputs)
            {
                var parameter = method.Parameters.FirstOrDefault(candidate => candidate.Name == output.Key) ??
                                throw new Refusal($"outputs names '{output.Key}', which is no parameter.");
                if (parameter.RefKind is not (RefKind.Out or RefKind.Ref))
                    throw new Refusal($"outputs parameter '{output.Key}' must be out or ref.");
                CheckResult(output.Value, parameter.Type);
            }
            if (result is not null)
                CheckResult(result, method.ReturnType);
        }

        private void CheckLeaves(LibraryValue value)
        {
            switch (value)
            {
                case ElementsValue elements:
                    CheckLeaves(elements.Source);
                    _ = TypeOf(elements);
                    break;
                case SequenceValue sequence:
                    foreach (var item in sequence.Values)
                        CheckLeaves(item);
                    break;
                case GroupingValue grouping:
                    CheckLeaves(grouping.Key);
                    CheckLeaves(grouping.Values);
                    break;
                default:
                    _ = TypeOf(value);
                    break;
            }
        }

        private void CheckResult(LibraryResult form, ITypeSymbol returnType)
        {
            switch (form.Kind)
            {
                case LibraryResultKind.New:
                    if (returnType.SpecialType == SpecialType.System_Void)
                        throw new Refusal("new is refused on a void member's result.");
                    break;
                case LibraryResultKind.Collection:
                    if (returnType is not IArrayTypeSymbol && !(returnType is INamedTypeSymbol named &&
                                                               Collections.Contains(MetadataName(named.OriginalDefinition))))
                        throw new Refusal($"collection(…) needs an array or a collection of ADR 0010; the member returns {returnType.ToDisplayString()}.");
                    ConvertAll(form.Values, ElementType(returnType)!);
                    break;
                case LibraryResultKind.Dictionary:
                    if (returnType is not INamedTypeSymbol { Arity: 2 } dictionary ||
                        MetadataName(dictionary.OriginalDefinition) != "System.Collections.Generic.Dictionary`2")
                        throw new Refusal($"dictionary(…) needs a Dictionary<TKey, TValue>; the member returns {returnType.ToDisplayString()}.");
                    Converts(form.Values[0], dictionary.TypeArguments[0]);
                    Converts(form.Values[1], dictionary.TypeArguments[1]);
                    break;
                case LibraryResultKind.Sequence:
                    if (returnType.TypeKind != TypeKind.Interface || ElementType(returnType) is not { } element ||
                        !IsGenericEnumerable(returnType) && !returnType.AllInterfaces.Any(IsGenericEnumerable))
                        throw new Refusal($"sequence(…) needs an interface that is or derives from IEnumerable<T>; the member returns {returnType.ToDisplayString()}.");
                    ConvertAll(form.Values, element);
                    break;
                case LibraryResultKind.OneOf:
                    if (returnType.SpecialType == SpecialType.System_Void)
                        throw new Refusal("[…] needs a member that returns something.");
                    ConvertAll(form.Values, returnType);
                    break;
                default:
                    throw new UnreachableException($"Unknown result kind {form.Kind}.");
            }
        }

        private void ConvertAll(IEnumerable<LibraryValue> values, ITypeSymbol destination)
        {
            foreach (var value in values)
                Converts(value, destination);
        }

        private void Converts(LibraryValue value, ITypeSymbol destination)
        {
            switch (value)
            {
                case SequenceValue sequence:
                    ConvertAll(sequence.Values, ElementType(destination) ?? destination);
                    return;
                case GroupingValue grouping:
                    var type = destination is INamedTypeSymbol named && MetadataName(named.OriginalDefinition) == "System.Linq.IGrouping`2"
                        ? named : destination.AllInterfaces.FirstOrDefault(candidate => MetadataName(candidate.OriginalDefinition) == "System.Linq.IGrouping`2");
                    Converts(grouping.Key, type?.TypeArguments[0] ?? destination);
                    Converts(grouping.Values, type?.TypeArguments[1] ?? destination);
                    return;
                case ElementsValue elements:
                    Projected(elements.Source, destination, 1);
                    return;
                case ArgumentValue or ReturnsValue or ThisValue or HolderArgumentValue or KeptValue:
                    break;
                case NewValue:
                    throw new Refusal("new is allowed only at the top level of a fate input.");
                default:
                    throw new UnreachableException($"Unknown value kind {value.GetType().Name}.");
            }
            var source = TypeOf(value);
            if (source is not null && !Converts(source, destination))
                throw new Refusal($"{value} is {source.ToDisplayString()}, which does not convert to {destination.ToDisplayString()}.");
        }

        private void Projected(LibraryValue value, ITypeSymbol destination, int depth)
        {
            if (depth == 0)
            {
                Converts(value, destination);
                return;
            }
            switch (value)
            {
                case SequenceValue sequence:
                    foreach (var item in sequence.Values)
                        Projected(item, destination, depth - 1);
                    break;
                case GroupingValue grouping:
                    Converts(grouping.Key, compilation.GetSpecialType(SpecialType.System_Object));
                    Projected(grouping.Values, destination, depth - 1);
                    break;
                case ElementsValue elements:
                    Projected(elements.Source, destination, depth + 1);
                    break;
                case ArgumentValue or ReturnsValue or ThisValue or HolderArgumentValue or KeptValue:
                    for (var step = 0; step < depth; step++)
                        value = new ElementsValue(value);
                    var source = TypeOf(value);
                    if (source is not null && !Converts(source, destination))
                        throw new Refusal($"{value} is {source.ToDisplayString()}, which does not convert to {destination.ToDisplayString()}.");
                    break;
                case NewValue:
                    throw new Refusal("new is allowed only at the top level of a fate input.");
                default:
                    throw new UnreachableException($"Unknown value kind {value.GetType().Name}.");
            }
        }

        private bool Converts(ITypeSymbol source, ITypeSymbol destination)
        {
            var conversion = compilation.ClassifyConversion(source, destination);
            return conversion.Exists && (conversion.IsIdentity || conversion.IsReference || conversion.IsBoxing || conversion.IsUnboxing);
        }

        private ITypeSymbol? TypeOf(LibraryValue value)
        {
            switch (value)
            {
                case ThisValue:
                    return TargetType("this");
                case KeptValue kept:
                    _ = TargetType(kept.Keeper);
                    return null;
                case ArgumentValue argument:
                    var parameter = method.Parameters.FirstOrDefault(candidate => candidate.Name == argument.Parameter) ??
                                    throw new Refusal($"{value} names no parameter.");
                    return IsDelegate(parameter.Type) ? throw new Refusal($"{value} names a delegate-typed parameter.") : parameter.Type;
                case ReturnsValue returns:
                    var invoke = ((INamedTypeSymbol)method.Parameters.First(candidate => candidate.Name == returns.Delegate).Type).DelegateInvokeMethod!;
                    return invoke.ReturnsVoid ? throw new Refusal($"{value} names a delegate that returns void.") : invoke.ReturnType;
                case ElementsValue elements:
                    var source = TypeOf(elements.Source);
                    return source is null ? null : ElementType(source) ?? throw new Refusal($"{value}: {source.ToDisplayString()} is not enumerable.");
                case SequenceValue or GroupingValue or HolderArgumentValue:
                    return null;
                case NewValue:
                    throw new Refusal("new is allowed only at the top level of a fate input.");
                default:
                    throw new UnreachableException($"Unknown value kind {value.GetType().Name}.");
            }
        }

        private ITypeSymbol TargetType(string target) => target == "this"
            ? !method.IsStatic ? method.ContainingType : throw new Refusal("this needs an instance member or constructor.")
            : method.Parameters.FirstOrDefault(parameter => parameter.Name == target)?.Type ?? throw new Refusal($"target '{target}' names a missing parameter.");

        private static void CheckNewInputType(ITypeSymbol type)
        {
            if (IsAlwaysDelegate(type))
                throw new Refusal("a fresh delegate would run code no model describes.");
            if (!type.IsReferenceType)
                throw new Refusal("new input needs a reference-typed parameter.");
        }

        private static bool IsAlwaysDelegate(ITypeSymbol type) => type switch
        {
            INamedTypeSymbol named when named.TypeKind == TypeKind.Delegate ||
                                       MetadataName(named.OriginalDefinition) is "System.Delegate" or "System.MulticastDelegate" => true,
            ITypeParameterSymbol parameter => parameter.ConstraintTypes.Any(IsAlwaysDelegate),
            _ => false
        };

        /// <summary>What enumerating a value of <paramref name="type"/> yields: an array's element type, the <c>T</c> of the
        /// <c>IEnumerable&lt;T&gt;</c> it is or implements, <c>object</c> for a non-generic <c>IEnumerable</c>; else null.</summary>
        private ITypeSymbol? ElementType(ITypeSymbol type) =>
            type is IArrayTypeSymbol array ? array.ElementType :
            IsGenericEnumerable(type) ? ((INamedTypeSymbol)type).TypeArguments[0] :
            type.AllInterfaces.FirstOrDefault(IsGenericEnumerable) is { } implemented ? implemented.TypeArguments[0] :
            type.SpecialType == SpecialType.System_Collections_IEnumerable ||
            type.AllInterfaces.Any(candidate => candidate.SpecialType == SpecialType.System_Collections_IEnumerable)
                ? compilation.GetSpecialType(SpecialType.System_Object)
                : null;

        private static bool IsGenericEnumerable(ITypeSymbol type) =>
            type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;

        private static string MetadataName(INamedTypeSymbol type) =>
            type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;
    }

    /// <summary>A recursive-descent reader of the value and result grammar; it allows no whitespace anywhere.</summary>
    internal sealed class ValueParser(string text)
    {
        private int _at;

        internal static T? Whole<T>(string text, Func<ValueParser, T?> read) where T : class
        {
            var parser = new ValueParser(text);
            return read(parser) is { } parsed && parser._at == text.Length ? parsed : null;
        }

        internal LibraryValue? Value()
        {
            if (Take("new"))
                return new NewValue();
            if (Take("this"))
                return new ThisValue();
            if (Take("kept:"))
                return Name() is { } keeper ? new KeptValue(keeper) : null;
            if (Take("arg:"))
                return Name() is { } parameter ? new ArgumentValue(parameter) : null;
            if (Take("returns:"))
                return Name() is { } name ? new ReturnsValue(name) : null;
            if (Take("holder-arg:"))
                return Number() is { } index ? new HolderArgumentValue(index) : null;
            if (Take("elements("))
                return Value() is { } source && Take(")") ? new ElementsValue(source) : null;
            if (Take("sequence("))
                return List(")") is { } values ? new SequenceValue(values) : null;
            if (Take("grouping("))
                return Value() is { } key && Take(",") && Value() is { } items && Take(")") ? new GroupingValue(key, items) : null;
            return null;
        }

        internal LibraryResult? Result()
        {
            if (Take("new"))
                return new LibraryResult(LibraryResultKind.New, []);
            if (Take("sequence("))
                return List(")") is { } values ? new LibraryResult(LibraryResultKind.Sequence, values) : null;
            if (Take("collection("))
                return List(")") is { } values ? new LibraryResult(LibraryResultKind.Collection, values) : null;
            if (Take("dictionary("))
                return Value() is { } key && Take(",") && Value() is { } item && Take(")")
                    ? new LibraryResult(LibraryResultKind.Dictionary, [key, item])
                    : null;
            if (Take("["))
                return List("]") is { } values ? new LibraryResult(LibraryResultKind.OneOf, values) : null;
            return null;
        }

        private List<LibraryValue>? List(string close)
        {
            var values = new List<LibraryValue>();
            do
            {
                if (Value() is not { } value)
                    return null;
                values.Add(value);
            } while (Take(","));
            return Take(close) ? values : null;
        }

        internal string? Name()
        {
            var start = _at;
            if (_at < text.Length && (char.IsAsciiLetter(text[_at]) || text[_at] == '_'))
                _at++;
            else
                return null;
            while (_at < text.Length && (char.IsAsciiLetterOrDigit(text[_at]) || text[_at] == '_'))
                _at++;
            return text[start.._at];
        }

        private int? Number()
        {
            var start = _at;
            while (_at < text.Length && char.IsAsciiDigit(text[_at]))
                _at++;
            var digits = text[start.._at];
            return digits.Length > 0 && (digits == "0" || digits[0] != '0') && int.TryParse(digits, out var value) ? value : null;
        }

        private bool Take(string expected)
        {
            if (!text.AsSpan(_at).StartsWith(expected, StringComparison.Ordinal))
                return false;
            _at += expected.Length;
            return true;
        }
    }
}
