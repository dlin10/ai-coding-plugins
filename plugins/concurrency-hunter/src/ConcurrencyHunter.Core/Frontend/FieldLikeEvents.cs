using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConcurrencyHunter.Frontend;

/// <summary>
/// Field-like events (ADR 0014): an event declared without accessors of its own is the delegate field the compiler declares for it,
/// and its accessors have the compiler's bodies over that field. This is the one answer to whether an event is one and to how
/// every access names its storage.
/// </summary>
internal static class FieldLikeEvents
{
    /// <summary>Whether an event is field-like: declared by a variable declarator of an event field declaration, not
    /// <c>abstract</c> and not <c>extern</c>.</summary>
    /// <param name="event">The event.</param>
    public static bool Is(IEventSymbol @event) =>
        !@event.IsAbstract && !@event.IsExtern && @event.DeclaringSyntaxReferences.Length != 0 &&
        @event.DeclaringSyntaxReferences.All(reference => reference.GetSyntax() is VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax });

    /// <summary>A field-like event's storage as every access names it. Roslyn lists no backing field among a type's members, so
    /// it is built from the event, equal to what <see cref="IrLowering.FieldRef"/> returns for the compiler's backing field.</summary>
    /// <param name="event">The field-like event.</param>
    public static IrFieldRef FieldRef(IEventSymbol @event) =>
        new(
            @event.ContainingAssembly.Name,
            SymbolNames.Type(@event.ContainingType),
            @event.Name,
            IrFieldKind.Field,
            @event.IsStatic,
            false,
            SymbolNames.Type(@event.Type),
            SymbolNames.TypeIdentity(@event.ContainingType))
        {
            IsVolatile = false,
            IsContainingTypeReadOnly = @event.ContainingType.IsReadOnly,
            FieldTypeKey = SymbolNames.TypeKey(@event.Type)
        };
}
