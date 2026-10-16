using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.CallGraph;

/// <summary>Which comparer object CoreLib's default-comparer factory makes for a type: the selection of <c>ComparerHelpers</c> in
/// CoreLib 8.0, which fills <c>Comparer&lt;T&gt;.Default</c> and <c>EqualityComparer&lt;T&gt;.Default</c> from each type's initializer. For
/// ordering, <c>GenericComparer&lt;X&gt;</c> when X is assignable to <c>IComparable&lt;X&gt;</c> — which, the interface being
/// contravariant, a reference type implementing it over a base, an interface of its own or <c>object</c> is —, else
/// <c>NullableComparer&lt;U&gt;</c> when X is <c>Nullable&lt;U&gt;</c>, else <c>EnumComparer&lt;X&gt;</c> for an enum, else
/// <c>ObjectComparer&lt;X&gt;</c>; for equality, <c>GenericEqualityComparer&lt;string&gt;</c> for <c>string</c>, else
/// <c>GenericEqualityComparer&lt;X&gt;</c> when X implements the invariant <c>IEquatable&lt;X&gt;</c> itself, else
/// <c>NullableEqualityComparer&lt;U&gt;</c>, else <c>EnumEqualityComparer&lt;X&gt;</c>, else <c>ObjectEqualityComparer&lt;X&gt;</c>;
/// <c>string</c> implements both generic interfaces, whether its declaration is source or metadata.
/// The places that answer it all ask this class: the lowering marks a call of either factory with a <c>typeof</c> argument of a closed
/// type or of a type parameter (<see cref="IrCallOperation.DefaultComparer"/>, <c>IrLowering</c>'s <c>DefaultComparerOf</c> and
/// <c>ComparerFactory</c>, which <c>IrLowering.RecognizerOf</c> claims and <c>EngineClaims</c> reads); the summary records the mark as the
/// allocation it is (<see cref="AllocationValue.DefaultComparer"/>, <see cref="CallTransfer.DefaultComparer"/>); the reachable set reaches,
/// at a mark, the constructor of <see cref="Of"/>'s comparer for each closed type the mark's type is bound to, records the type
/// (<see cref="ReachableSetResult.DefaultComparerTypes"/>) and, under <see cref="DispatchRule.ConstructedTypes"/>, counts the comparer
/// constructed, and reaches the call the mark replaced only where the type can stay open in an instance of the heap
/// (<c>ReachableSet</c>'s <c>ReachComparers</c> and <c>ReachComparer</c>, over the walker's closed bindings and its type parameters that can
/// stay open, <c>MarkOpen</c>); and the heap creates <see cref="Of"/>'s comparer in each instance whose substitution closes the type to one
/// the reachable set recorded and binds its constructor in place of the call (<c>WholeProgram</c>'s <c>DefaultComparer</c> and <c>Call</c>).
/// <see cref="Bindings"/> owns the instance decision: an open type, including a receiver- or call-site-driven merged context, makes
/// each closed binding's comparer recorded for this mark alone (<see cref="ReachableSetResult.DefaultComparerBindings"/>); the ordinary
/// factory runs only for a mark reachability records as possibly open (<see cref="ReachableSetResult.OpenDefaultComparerCalls"/>).
/// A type parameter can stay open where it is bound to more closed types than <see cref="Heap.AnalysisLimits.MaxContextsPerMethod"/>,
/// which the heap merges, or over a type argument naming such a parameter. Past that count the walker records no further binding,
/// keeping recursive widening finite; no closed binding is discarded for its nesting depth.
/// Receiver and call-site merges preserve the recorded closed selections without making the reachable set predict heap contexts.</summary>
public static class DefaultComparers
{
    /// <summary>The assembly declaring the factory and every comparer it makes.</summary>
    public const string CORELIB = "System.Private.CoreLib";

    /// <summary>The type declaring the two factories, by metadata name.</summary>
    public const string HELPERS = "System.Collections.Generic.ComparerHelpers";

    private const string NAMESPACE = "System.Collections.Generic.";

    private static readonly string[] Ordering = ["GenericComparer", "NullableComparer", "EnumComparer", "ObjectComparer"];
    private static readonly string[] Equality = ["GenericEqualityComparer", "NullableEqualityComparer", "EnumEqualityComparer", "ObjectEqualityComparer"];

    /// <summary>The comparer the factory of <paramref name="kind"/> makes for a closed type, as a type key.</summary>
    /// <param name="program">The scope's types, which say what the type implements and derives from.</param>
    /// <param name="kind">The factory.</param>
    /// <param name="typeKey">The closed type the <c>typeof</c> names.</param>
    public static string Of(ProgramIndex program, IrComparerKind kind, string typeKey)
    {
        var held = TypeSafety.NullableValueType(typeKey);
        // String implements both generic interfaces over itself, which the index knows only where it holds string's declaration.
        if (kind == IrComparerKind.Ordering)
        {
            return IsString(typeKey) || Implements(program, typeKey, "System.IComparable`1", contravariant: true) ? Key(Ordering[0], typeKey)
                : held is not null ? Key(Ordering[1], held)
                : IsEnum(program, typeKey) ? Key(Ordering[2], typeKey)
                : Key(Ordering[3], typeKey);
        }

        return IsString(typeKey) || Implements(program, typeKey, "System.IEquatable`1", contravariant: false) ? Key(Equality[0], typeKey)
            : held is not null ? Key(Equality[1], held)
            : IsEnum(program, typeKey) ? Key(Equality[2], typeKey)
            : Key(Equality[3], typeKey);
    }

    /// <summary>Whether a method is one of the two factories: declared by <see cref="HELPERS"/> of <see cref="CORELIB"/>, by its name and
    /// one parameter.</summary>
    /// <param name="containingType">The metadata name of the method's containing type, namespace included.</param>
    /// <param name="assembly">The name of the assembly declaring it.</param>
    /// <param name="name">The method's name.</param>
    /// <param name="parameters">The number of its parameters.</param>
    public static IrComparerKind? FactoryOf(string containingType, string assembly, string name, int parameters) =>
        containingType != HELPERS || assembly != CORELIB || parameters != 1 ? null
        : name switch
        {
            "CreateDefaultComparer" => IrComparerKind.Ordering,
            "CreateDefaultEqualityComparer" => IrComparerKind.Equality,
            _ => null
        };

    private static string Key(string comparer, string argument) => $"{CORELIB}:{NAMESPACE}{comparer}<{argument}>";

    /// <summary>The closed selections an instance makes: its substituted type when recorded, or every recorded closed binding of the
    /// mark where merging left the type open. The heap's allocation evaluation and call construction use this answer, as does the
    /// generator's <c>ComparisonWitnesses.Add</c> for the recorded probe bindings of Default getter and initializer marks.</summary>
    /// <param name="program">The scope's type index.</param>
    /// <param name="reachable">The closed types and open calls reachability recorded.</param>
    /// <param name="bodyId">The body holding the mark.</param>
    /// <param name="operation">The marked call.</param>
    /// <param name="typeKey">The mark's type after the instance's substitution.</param>
    public static IEnumerable<string> Bindings(ProgramIndex program, ReachableSetResult reachable, string bodyId, int operation, string typeKey) =>
        program.IsOpen(typeKey)
            ? (reachable.DefaultComparerBindings.GetValueOrDefault((bodyId, operation)) ?? new HashSet<string>()).Order(StringComparer.Ordinal)
            : reachable.DefaultComparerBindings.GetValueOrDefault((bodyId, operation))?.Contains(typeKey) == true ? [typeKey] : [];

    /// <summary>Whether a type is assignable to the generic interface over itself, wherever that interface is declared, as CoreLib asks
    /// <c>typeof(I&lt;&gt;).MakeGenericType(X).IsAssignableFrom(X)</c>: X implements <c>I&lt;X&gt;</c>, or, for a contravariant interface
    /// (<c>IComparable&lt;in T&gt;</c>) and a reference type X, <c>I&lt;A&gt;</c> for a type A X converts to by a reference conversion — a
    /// base class, an interface X has, <c>object</c>, including conversions using variance inside A. A value type converts to none of them by a reference conversion, so variance gives it
    /// nothing, and an invariant interface (<c>IEquatable&lt;T&gt;</c>) needs <c>I&lt;X&gt;</c> itself. The frontend's compiler conversion
    /// classification supplies the answer for every symbol it can reconstruct, including arrays of any rank, nested generic types,
    /// Nullable and tuples. Only a type key it cannot build a symbol for (or an index without a frontend) falls back to listed supertypes.
    /// Reachability's <c>ReachComparer</c> and the heap's <c>DefaultComparer</c> share this decision through <see cref="Of"/>.</summary>
    /// <param name="program">The scope's types.</param>
    /// <param name="typeKey">The type.</param>
    /// <param name="definition">The interface's metadata name, namespace included.</param>
    /// <param name="contravariant">Whether the interface's type parameter is contravariant.</param>
    private static bool Implements(ProgramIndex program, string typeKey, string definition, bool contravariant)
    {
        if (program.ImplementsSelfInterface?.Invoke(typeKey, definition) is bool classified)
            return classified;
        var supertypes = program.Supertypes(typeKey).ToArray();
        var reference = contravariant && program.Type(program.Decompose(typeKey).DefinitionKey) is { IsValueType: false };
        return supertypes.Any(supertype => Named(TypeSafety.DefinitionOf(supertype), definition) &&
                                           ProgramIndex.Shape(supertype).Arguments is [var argument] &&
                                           (argument == typeKey ||
                                            reference && (supertypes.Contains(argument, StringComparer.Ordinal) || Named(argument, "System.Object") ||
                                                          Named(argument, "object"))));
    }

    private static bool IsEnum(ProgramIndex program, string typeKey) =>
        program.Supertypes(typeKey).Any(supertype => Named(supertype, "System.Enum"));

    private static bool IsString(string typeKey) => Named(typeKey, "string") || Named(typeKey, "System.String");

    /// <summary>Whether a type key or definition names a type by its full name, whatever assembly declares it.</summary>
    /// <param name="key">The type key or definition.</param>
    /// <param name="name">The full name.</param>
    private static bool Named(string key, string name) => key.EndsWith(":" + name, StringComparison.Ordinal) || key == name;
}
