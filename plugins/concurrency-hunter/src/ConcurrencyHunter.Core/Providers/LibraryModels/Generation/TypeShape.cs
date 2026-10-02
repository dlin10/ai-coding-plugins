using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>What a value of a type can carry, as the model generator's driver reads it (SPEC TD-034b, G-0).</summary>
public enum TypeShapeKind
{
    /// <summary>A <c>ref struct</c>, a pointer or a function pointer: no driver can hold one in a field.</summary>
    RefLikeOrPointer,

    /// <summary>A delegate type.</summary>
    Delegate,

    /// <summary><c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c>.</summary>
    TaskOfT,

    /// <summary><c>Task</c> or <c>ValueTask</c>.</summary>
    Task,

    /// <summary>A primitive, an enum, or an immutable type of the built-in models (TD-034a).</summary>
    Immutable,

    /// <summary>A struct whose fields, recursively, hold no reference.</summary>
    PlainStruct,

    /// <summary>Any other struct.</summary>
    StructWithReferences,

    /// <summary>Any other type: a class, an interface, an array or a type parameter.</summary>
    Reference
}

/// <summary>The one owner of a type's <see cref="TypeShapeKind"/>.</summary>
public static class TypeShape
{
    private const string TASKS = "System.Threading.Tasks";

    /// <summary>The shape of a type: the first that applies, in the order of <see cref="TypeShapeKind"/>.</summary>
    /// <param name="type">The type.</param>
    public static TypeShapeKind Of(ITypeSymbol type)
    {
        if (type.IsRefLikeType || type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
            return TypeShapeKind.RefLikeOrPointer;
        if (type.TypeKind == TypeKind.Delegate)
            return TypeShapeKind.Delegate;
        if (type is INamedTypeSymbol { ContainingNamespace: { } ns, ContainingType: null } named && ns.ToDisplayString() == TASKS &&
            named.Name is "Task" or "ValueTask")
        {
            return named.Arity switch
            {
                0 => TypeShapeKind.Task,
                1 => TypeShapeKind.TaskOfT,
                _ => TypeShapeKind.Reference
            };
        }

        if (IsPrimitive(type) || type.TypeKind == TypeKind.Enum || LibraryModels.BuiltIn.IsImmutable(type))
            return TypeShapeKind.Immutable;
        if (type.TypeKind == TypeKind.Struct)
            return type.IsUnmanagedType ? TypeShapeKind.PlainStruct : TypeShapeKind.StructWithReferences;
        return TypeShapeKind.Reference;
    }

    private static bool IsPrimitive(ITypeSymbol type) => type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char
        or SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16
        or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
        or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_String
        or SpecialType.System_IntPtr or SpecialType.System_UIntPtr;
}
