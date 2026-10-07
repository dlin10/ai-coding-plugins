using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels;

/// <summary>The tasks a type is, level by level: <c>Task&lt;T&gt;</c> and <c>ValueTask&lt;T&gt;</c> complete with T, at every level
/// of nesting. The one owner of looking through a task type: the vocabulary's checks, the lowering and the generator read it.</summary>
internal static class TaskTypes
{
    private const string TASK = "System.Threading.Tasks.Task";
    private const string TASK_T = "System.Threading.Tasks.Task`1";
    private const string VALUE_TASK = "System.Threading.Tasks.ValueTask";
    private const string VALUE_TASK_T = "System.Threading.Tasks.ValueTask`1";

    /// <summary>The type a <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c> completes with; null for any other type, the
    /// non-generic <c>Task</c> and <c>ValueTask</c> among them.</summary>
    /// <param name="type">The type, or null.</param>
    internal static ITypeSymbol? CompletionType(ITypeSymbol? type) =>
        type is INamedTypeSymbol { Arity: 1 } named && Name(named) is TASK_T or VALUE_TASK_T ? named.TypeArguments[0] : null;

    /// <summary>How many <c>Task&lt;…&gt;</c> or <c>ValueTask&lt;…&gt;</c> levels stand around the type's innermost type.</summary>
    /// <param name="type">The type.</param>
    internal static int Depth(ITypeSymbol type)
    {
        var depth = 0;
        for (var inner = CompletionType(type); inner is not null; inner = CompletionType(inner))
            depth++;
        return depth;
    }

    /// <summary>The type the innermost task completes with, or the type itself when it is no <c>Task&lt;T&gt;</c> or
    /// <c>ValueTask&lt;T&gt;</c>.</summary>
    /// <param name="type">The type.</param>
    internal static ITypeSymbol Innermost(ITypeSymbol type)
    {
        while (CompletionType(type) is { } inner)
            type = inner;
        return type;
    }

    /// <summary>Whether the type is the non-generic <c>Task</c> or <c>ValueTask</c>, which completes with no value.</summary>
    /// <param name="type">The type, or null.</param>
    internal static bool IsValueless(ITypeSymbol? type) => type is INamedTypeSymbol { Arity: 0 } named && Name(named) is TASK or VALUE_TASK;

    private static string Name(INamedTypeSymbol type) =>
        type.OriginalDefinition.ContainingNamespace.ToDisplayString() + "." + type.OriginalDefinition.MetadataName;
}
