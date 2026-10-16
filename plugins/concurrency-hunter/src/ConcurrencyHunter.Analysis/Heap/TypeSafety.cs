using ConcurrencyHunter.CallGraph;

namespace ConcurrencyHunter.Heap;

/// <summary>Checks a member's declaring type against only objects whose runtime type is known exactly. An object of an open-world
/// assembly stands for an object of any type a program could declare, so it is no other type only when no such type can be both: a
/// sealed class, a value type, or a type another assembly cannot name (Open-world rule).</summary>
/// <param name="program">The scope's types.</param>
/// <param name="metadataSupertypes">The supertypes of each type from metadata, by definition.</param>
/// <param name="openWorldAssemblies">The assemblies whose objects stand for an object of any type
/// (<see cref="ReachabilityRules.OpenWorldAssemblies"/>); none when <c>null</c>.</param>
public sealed class TypeSafety(ProgramIndex program, IReadOnlyDictionary<string, IReadOnlySet<string>> metadataSupertypes,
                               IReadOnlySet<string>? openWorldAssemblies = null)
{
    /// <summary>The definitions of the supertypes of each type the program index holds, which the index alone decides.</summary>
    private readonly Dictionary<string, IReadOnlySet<string>> _supertypes = new(StringComparer.Ordinal);

    public static string DefinitionOf(string typeKey)
    {
        var arrayStart = typeKey.LastIndexOf('[');
        if (arrayStart >= 0 && typeKey.EndsWith(']') &&
            typeKey.AsSpan(arrayStart + 1, typeKey.Length - arrayStart - 2).IndexOfAnyExcept(',') < 0)
            return typeKey[arrayStart..];

        var definition = new System.Text.StringBuilder();
        for (var index = 0; index < typeKey.Length; index++)
        {
            if (typeKey[index] != '<')
            {
                definition.Append(typeKey[index]);
                continue;
            }

            var depth = 1;
            var bracketDepth = 0;
            var count = 1;
            while (++index < typeKey.Length && depth != 0)
            {
                if (typeKey[index] == '<')
                    depth++;
                else if (typeKey[index] == '>')
                    depth--;
                else if (typeKey[index] == '[')
                    bracketDepth++;
                else if (typeKey[index] == ']')
                    bracketDepth--;
                else if (typeKey[index] == ',' && depth == 1 && bracketDepth == 0)
                    count++;
            }

            definition.Append('`').Append(count);
            index--;
        }

        return definition.ToString();
    }

    public bool CannotBe(HeapRegion region, string declaringTypeKey)
    {
        if (!region.HasExactType || region.TypeKey is null)
            return false;

        var declaring = DefinitionOf(declaringTypeKey);
        if (declaring is "object" || declaring.EndsWith(":object", StringComparison.Ordinal) ||
            declaring.EndsWith("]object", StringComparison.Ordinal) ||
            declaring.EndsWith(":System.Object", StringComparison.Ordinal) ||
            declaring.EndsWith("]System.Object", StringComparison.Ordinal))
            return false;

        // A Nullable is the value it holds, so a member of Nullable<S> runs on a region of S, or of the type a generic instance binds to
        // its T; a T no instance binds yet may be any.
        if (NullableValueType(declaringTypeKey) is { } held && (held == region.TypeKey || program.IsOpen(held)))
            return false;

        var definition = DefinitionOf(region.TypeKey);
        // A span made from an array is the array: the conversion carries it, so a member of the span runs on the array's region.
        if (definition == "[]" && (declaring.EndsWith(":System.Span`1", StringComparison.Ordinal) ||
                                   declaring.EndsWith(":System.ReadOnlySpan`1", StringComparison.Ordinal)))
        {
            return false;
        }

        IReadOnlySet<string>? supertypes = null;
        if (program.Type(region.TypeKey) is not null)
        {
            if (!_supertypes.TryGetValue(region.TypeKey, out supertypes))
                _supertypes.Add(region.TypeKey, supertypes = program.Supertypes(region.TypeKey).Select(DefinitionOf).ToHashSet(StringComparer.Ordinal));
        }
        else
            metadataSupertypes.TryGetValue(definition, out supertypes);

        if (supertypes is null || supertypes.Contains(declaring))
            return false;
        return !StandsForAnyType(region.TypeKey) ||
               program.Type(declaringTypeKey) is { IsSealed: true } or { IsValueType: true } or { IsVisibleOutside: false };
    }

    /// <summary>The type a <c>Nullable&lt;S&gt;</c> holds, S, or null for any other type. The engine holds a <c>Nullable</c> as the value it
    /// holds, so a region of S is the receiver of a member of <c>Nullable&lt;S&gt;</c> (<see cref="CannotBe"/>), and the instance such a
    /// call binds takes its type arguments from the type the call names (<c>WholeProgram</c>'s <c>ReceiverSubstitution</c>).</summary>
    /// <param name="typeKey">The type key.</param>
    public static string? NullableValueType(string typeKey) =>
        DefinitionOf(typeKey) is var definition && (definition == "System.Nullable`1" || definition.EndsWith(":System.Nullable`1", StringComparison.Ordinal)) &&
        ProgramIndex.Shape(typeKey).Arguments is [var held]
            ? held
            : null;

    /// <summary>Whether an object of the type stands for an object of any type: the type belongs to an open-world assembly.</summary>
    /// <param name="typeKey">The object's type.</param>
    private bool StandsForAnyType(string typeKey) =>
        openWorldAssemblies is { Count: > 0 } && typeKey.IndexOf(':') is > 0 and var colon && openWorldAssemblies.Contains(typeKey[..colon]);
}
