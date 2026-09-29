using ConcurrencyHunter.CallGraph;

namespace ConcurrencyHunter.Heap;

/// <summary>Checks a member's declaring type against only objects whose runtime type is known exactly.</summary>
public sealed class TypeSafety(ProgramIndex program, IReadOnlyDictionary<string, IReadOnlySet<string>> metadataSupertypes)
{
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

        var definition = DefinitionOf(region.TypeKey);
        IReadOnlySet<string>? supertypes = null;
        if (program.Type(region.TypeKey) is not null)
            supertypes = program.Supertypes(region.TypeKey).Select(DefinitionOf).ToHashSet(StringComparer.Ordinal);
        else
            metadataSupertypes.TryGetValue(definition, out supertypes);

        return supertypes is not null && !supertypes.Contains(declaring);
    }
}
