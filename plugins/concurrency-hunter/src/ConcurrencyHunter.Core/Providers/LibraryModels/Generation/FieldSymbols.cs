using System.Runtime.CompilerServices;
using System.Text;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The one owner of the symbol a field the heap names stands for. An <see cref="IrFieldRef"/> names its containing type
/// by <see cref="SymbolNames.TypeIdentity"/>, which is no declaration id once the type is generic, so the type is found by that
/// same identity among the types of the field's assembly, never by gluing it into an <c>F:</c> or <c>E:</c> id.</summary>
public static class FieldSymbols
{
    private static readonly ConditionalWeakTable<IAssemblySymbol, ILookup<string, INamedTypeSymbol>> Types = new();

    /// <summary>The field, or the field-like event whose storage the field is, that a field reference names, or <c>null</c>.</summary>
    /// <param name="field">The field reference.</param>
    /// <param name="compilation">The compilation whose assemblies declare it.</param>
    public static ISymbol? Of(IrFieldRef field, Compilation compilation)
    {
        if (Assembly(field.Assembly, compilation) is not { } assembly)
            return null;
        var members = Types.GetValue(assembly, Index)[Shape(field.ContainingTypeId)].SelectMany(type => type.GetMembers(field.Name)).ToArray();
        return members.OfType<IFieldSymbol>().FirstOrDefault() ?? (ISymbol?)members.OfType<IEventSymbol>().FirstOrDefault();
    }

    /// <summary>The type a field, or the storage of a field-like event, holds, or <c>null</c>.</summary>
    /// <param name="field">The field reference.</param>
    /// <param name="compilation">The compilation whose assemblies declare it.</param>
    public static ITypeSymbol? TypeOf(IrFieldRef field, Compilation compilation) => Of(field, compilation) switch
    {
        IFieldSymbol symbol => symbol.Type,
        IEventSymbol symbol => symbol.Type,
        _ => null
    };

    private static IAssemblySymbol? Assembly(string name, Compilation compilation) =>
        compilation.Assembly.Name == name ? compilation.Assembly
            : compilation.SourceModule.ReferencedAssemblySymbols.FirstOrDefault(assembly => assembly.Name == name);

    private static ILookup<string, INamedTypeSymbol> Index(IAssemblySymbol assembly) =>
        AllTypes(assembly.GlobalNamespace).ToLookup(type => Shape(SymbolNames.TypeIdentity(type)), StringComparer.Ordinal);

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol @namespace)
            {
                foreach (var type in AllTypes(@namespace))
                    yield return type;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in AllTypes(type))
                    yield return nested;
            }
        }
    }

    /// <summary>A type identity without its type arguments but with the arity of every generic part, so <c>Pub</c> and
    /// <c>Pub&lt;T&gt;</c> stay apart while <c>Pub&lt;T&gt;</c> and <c>Pub&lt;int&gt;</c> meet.</summary>
    /// <param name="identity">A type identity.</param>
    private static string Shape(string identity)
    {
        var shape = new StringBuilder();
        var depth = 0;
        var arity = 0;
        var brackets = 0;
        foreach (var character in identity)
        {
            if (character == '[')
                brackets++;
            else if (character == ']')
                brackets--;
            if (character == '<')
            {
                if (depth++ == 0)
                    arity = 1;
            }
            else if (character == '>')
            {
                if (--depth == 0)
                    shape.Append('`').Append(arity);
            }
            else if (depth == 1 && brackets == 0 && character == ',')
            {
                arity++;
            }
            else if (depth == 0)
            {
                shape.Append(character);
            }
        }
        return shape.ToString();
    }
}
