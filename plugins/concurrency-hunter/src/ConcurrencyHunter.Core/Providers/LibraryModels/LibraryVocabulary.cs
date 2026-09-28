using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ConcurrencyHunter.Providers.LibraryModels;

/// <summary>Where a delegate a known call receives runs (ADR 0012): at the call, where the library sequence it makes is enumerated,
/// where a member of its holder is called, in startup, or in an unknown execution.</summary>
public enum LibraryFateKind { InvokeNow, Iterator, Holder, Startup, UnknownExecution }

/// <summary>The object that keeps a <see cref="LibraryFateKind.Holder"/> delegate: what the call returns, or its receiver.</summary>
public enum LibraryHolderKind { Result, This }

/// <summary>The fate of one delegate parameter, with what each parameter of the delegate's <c>Invoke</c> is handed. Null
/// <see cref="Inputs"/> hands every parameter nothing known, as an empty set for each does.</summary>
public sealed record LibraryFate(string Parameter, LibraryFateKind Kind, LibraryHolderKind? Holder,
                                 IReadOnlyList<IReadOnlyList<LibraryValue>>? Inputs)
{
    internal static string Text(LibraryFateKind kind) => kind switch
    {
        LibraryFateKind.InvokeNow => "invoke-now",
        LibraryFateKind.Iterator => "iterator",
        LibraryFateKind.Holder => "holder",
        LibraryFateKind.Startup => "startup",
        _ => "unknown-execution"
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

/// <summary><c>arg:P</c>: what the argument of parameter P points to.</summary>
public sealed record ArgumentValue(string Parameter) : LibraryValue
{
    public override string ToString() => "arg:" + Parameter;
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

public enum LibraryResultKind { Sequence, Collection, Dictionary, OneOf }

/// <summary>What a known call returns: a new library sequence, a new collection or array, a new dictionary (keys, then values), or
/// one of the objects the values name.</summary>
public sealed record LibraryResult(LibraryResultKind Kind, IReadOnlyList<LibraryValue> Values)
{
    /// <summary>The result <paramref name="text"/> writes, or null when it breaks the grammar anywhere.</summary>
    public static LibraryResult? Parse(string text) => LibraryVocabulary.ValueParser.Whole(text, parser => parser.Result());

    public override string ToString() => Kind switch
    {
        LibraryResultKind.Sequence => $"sequence({string.Join(',', Values)})",
        LibraryResultKind.Collection => $"collection({string.Join(',', Values)})",
        LibraryResultKind.Dictionary => $"dictionary({string.Join(',', Values)})",
        _ => $"[{string.Join(',', Values)}]"
    };

    /// <summary>The text two results that mean the same share: a dictionary's keys and values apart, any other result's values as
    /// a set.</summary>
    internal string Canonical => Kind == LibraryResultKind.Dictionary
        ? $"dictionary({string.Join(',', Values.Select(value => value.Canonical))})"
        : $"{Kind}({Set(Values)})";

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
    internal static (LibraryResult? Result, IReadOnlyList<LibraryFate> Fates) Entry(string? resultText, IReadOnlyList<RawFate> raw)
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
            var inputs = fate.Inputs?.Select(input => (IReadOnlyList<LibraryValue>)input.Select(text =>
                             LibraryValue.Parse(text) ?? throw new FormatException($"fate of '{fate.Parameter}': input '{text}' does not parse."))
                         .ToArray()).ToArray();
            if (!fates.TryAdd(fate.Parameter, new LibraryFate(fate.Parameter, kind, holder, inputs)))
                throw new FormatException($"fates name '{fate.Parameter}' twice.");
        }

        foreach (var fate in fates.Values)
            foreach (var value in fate.Inputs?.SelectMany(input => input) ?? [])
                Place(value, fates, fate, result: null, whole: true);
        foreach (var value in result?.Values ?? [])
            Place(value, fates, into: null, result, whole: true);
        if (fates.Values.Any(fate => fate.Kind == LibraryFateKind.Iterator) && result?.Kind != LibraryResultKind.Sequence)
            throw new FormatException("an iterator fate needs a sequence(…) result.");
        if (result is not null && fates.Values.Any(fate => fate.Holder == LibraryHolderKind.Result))
            throw new FormatException("an entry whose holder is the result carries no result.");
        return (result, fates.Values.ToArray());
    }

    /// <summary>Checks where a value stands: in the inputs of <paramref name="into"/>, or in <paramref name="result"/>.</summary>
    private static void Place(LibraryValue value, IReadOnlyDictionary<string, LibraryFate> fates, LibraryFate? into, LibraryResult? result,
                              bool whole)
    {
        switch (value)
        {
            case HolderArgumentValue when into?.Kind != LibraryFateKind.Holder || !whole:
                throw new FormatException($"{value} stands only alone in the inputs of a holder fate.");
            case ReturnsValue returns:
                if (!fates.TryGetValue(returns.Delegate, out var source))
                    throw new FormatException($"{value} names no fate of the entry.");
                var available = source.Kind switch
                {
                    LibraryFateKind.InvokeNow => true,
                    LibraryFateKind.Iterator => result?.Kind == LibraryResultKind.Sequence ||
                                                into is { Kind: LibraryFateKind.Iterator } && into.Parameter != source.Parameter,
                    _ => false
                };
                if (!available)
                    throw new FormatException($"{value}: the runs of a {LibraryFate.Text(source.Kind)} delegate are not there to be had here.");
                break;
            case ElementsValue elements:
                Place(elements.Source, fates, into, result, whole: false);
                break;
            case SequenceValue sequence:
                foreach (var item in sequence.Values)
                    Place(item, fates, into, result, whole: false);
                break;
            case GroupingValue grouping:
                Place(grouping.Key, fates, into, result, whole: false);
                Place(grouping.Values, fates, into, result, whole: false);
                break;
        }
    }

    /// <summary>The member step on the member's original definition, or null when the entry fits it.</summary>
    internal static string? Member(LibraryResult? result, IReadOnlyList<LibraryFate> fates, IMethodSymbol method, Compilation compilation)
    {
        try
        {
            new MemberCheck(result, fates, method, compilation).Run();
            return null;
        }
        catch (Refusal refusal)
        {
            return refusal.Message;
        }
    }

    /// <summary>Whether two entries' results and fates say the same, whatever order anything is written in; absent inputs are an
    /// empty set for every parameter.</summary>
    internal static bool Alike(LibraryResult? resultA, IReadOnlyList<LibraryFate> fatesA, LibraryResult? resultB, IReadOnlyList<LibraryFate> fatesB)
    {
        if (resultA?.Canonical != resultB?.Canonical || fatesA.Count != fatesB.Count)
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
        return true;
    }

    private static string InputSet(LibraryFate fate, int index) =>
        fate.Inputs is { } inputs && index < inputs.Count ? LibraryResult.Set(inputs[index]) : "";

    private static bool IsDelegate(ITypeSymbol type) => type.TypeKind == TypeKind.Delegate;

    private sealed class Refusal(string reason) : Exception(reason);

    private sealed class MemberCheck(LibraryResult? result, IReadOnlyList<LibraryFate> fates, IMethodSymbol method, Compilation compilation)
    {
        internal void Run()
        {
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
                        if (value is not HolderArgumentValue)
                            Converts(value, invoke.Parameters[index].Type);
                }
            }
            if (result is null)
                return;
            var returnType = method.ReturnType;
            switch (result.Kind)
            {
                case LibraryResultKind.Collection:
                    if (returnType is not IArrayTypeSymbol && !(returnType is INamedTypeSymbol named &&
                                                               Collections.Contains(MetadataName(named.OriginalDefinition))))
                        throw new Refusal($"collection(…) needs an array or a collection of ADR 0010; the member returns {returnType.ToDisplayString()}.");
                    ConvertAll(result.Values, ElementType(returnType)!);
                    break;
                case LibraryResultKind.Dictionary:
                    if (returnType is not INamedTypeSymbol { Arity: 2 } dictionary ||
                        MetadataName(dictionary.OriginalDefinition) != "System.Collections.Generic.Dictionary`2")
                        throw new Refusal($"dictionary(…) needs a Dictionary<TKey, TValue>; the member returns {returnType.ToDisplayString()}.");
                    Converts(result.Values[0], dictionary.TypeArguments[0]);
                    Converts(result.Values[1], dictionary.TypeArguments[1]);
                    break;
                case LibraryResultKind.Sequence:
                    if (returnType.TypeKind != TypeKind.Interface || ElementType(returnType) is not { } element ||
                        !IsGenericEnumerable(returnType) && !returnType.AllInterfaces.Any(IsGenericEnumerable))
                        throw new Refusal($"sequence(…) needs an interface that is or derives from IEnumerable<T>; the member returns {returnType.ToDisplayString()}.");
                    ConvertAll(result.Values, element);
                    break;
                default:
                    if (method.ReturnsVoid)
                        throw new Refusal("[…] needs a member that returns something.");
                    ConvertAll(result.Values, returnType);
                    break;
            }
        }

        private void ConvertAll(IEnumerable<LibraryValue> values, ITypeSymbol destination)
        {
            foreach (var value in values)
                Converts(value, destination);
        }

        private void Converts(LibraryValue value, ITypeSymbol destination)
        {
            var source = TypeOf(value);
            if (!Converts(source, destination))
                throw new Refusal($"{value} is {source.ToDisplayString()}, which does not convert to {destination.ToDisplayString()}.");
        }

        private bool Converts(ITypeSymbol source, ITypeSymbol destination)
        {
            var conversion = compilation.ClassifyConversion(source, destination);
            return conversion.Exists && (conversion.IsIdentity || conversion.IsReference || conversion.IsBoxing || conversion.IsUnboxing);
        }

        private ITypeSymbol TypeOf(LibraryValue value)
        {
            switch (value)
            {
                case ArgumentValue argument:
                    var parameter = method.Parameters.FirstOrDefault(candidate => candidate.Name == argument.Parameter) ??
                                    throw new Refusal($"{value} names no parameter.");
                    return IsDelegate(parameter.Type) ? throw new Refusal($"{value} names a delegate-typed parameter.") : parameter.Type;
                case ReturnsValue returns:
                    var invoke = ((INamedTypeSymbol)method.Parameters.First(candidate => candidate.Name == returns.Delegate).Type).DelegateInvokeMethod!;
                    return invoke.ReturnsVoid ? throw new Refusal($"{value} names a delegate that returns void.") : invoke.ReturnType;
                case ElementsValue elements:
                    var source = TypeOf(elements.Source);
                    return ElementType(source) ?? throw new Refusal($"{value}: {source.ToDisplayString()} is not enumerable.");
                case SequenceValue sequence:
                    var first = TypeOf(sequence.Values[0]);
                    foreach (var later in sequence.Values.Skip(1))
                        Converts(later, first);
                    return compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T).Construct(first);
                case GroupingValue grouping:
                    var type = compilation.GetTypeByMetadataName("System.Linq.IGrouping`2") ??
                               throw new Refusal($"{value}: System.Linq.IGrouping`2 is not referenced.");
                    return type.Construct(TypeOf(grouping.Key), TypeOf(grouping.Values));
                default:
                    throw new Refusal($"{value} has no type and stands only alone in a holder's inputs.");
            }
        }

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
