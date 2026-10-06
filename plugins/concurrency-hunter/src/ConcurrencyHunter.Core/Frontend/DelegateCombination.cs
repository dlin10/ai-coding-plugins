using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Frontend;

/// <summary>
/// A predefined combination or removal of delegates (R3), the one answer every lowering of a binary operator, a compound assignment and
/// an invocation asks for: <c>a + b</c>, <c>a - b</c>, <c>+=</c> and <c>-=</c> on a delegate-typed target, and <c>Delegate.Combine</c>,
/// <c>Remove</c> and <c>RemoveAll</c>. A user-defined operator is never one, whatever it returns.
/// </summary>
/// <param name="Removes">Whether it removes its later operand from its first rather than combining them.</param>
/// <param name="Operands">The operand operations in parameter order: the left operand, a compound target or <c>source</c> first.
/// For a <c>Combine</c> of delegates listed at the call they are the listed elements; for one of an existing array, the array.</param>
internal sealed record DelegateCombination(bool Removes, IReadOnlyList<IOperation> Operands)
{
    /// <summary>The array creation or collection expression the operands are listed in, null where they are arguments or
    /// operands of their own.</summary>
    public IOperation? ListedIn { get; init; }

    /// <summary>Whether the one operand is an existing array whose cells are the delegates combined: the combination's operand is
    /// then what a read of those cells loads.</summary>
    public bool CombinesCells { get; init; }

    /// <summary>The combination <paramref name="operation"/> is, null for any other operation.</summary>
    /// <param name="operation">A binary operation, a compound assignment or an invocation.</param>
    public static DelegateCombination? Of(IOperation operation) => operation switch
    {
        IBinaryOperation { OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract, OperatorMethod: null } binary
            when IsDelegate(binary.Type) =>
            new(binary.OperatorKind == BinaryOperatorKind.Subtract, [binary.LeftOperand, binary.RightOperand]),
        ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract, OperatorMethod: null } compound
            when IsDelegate(compound.Target.Type) =>
            new(compound.OperatorKind == BinaryOperatorKind.Subtract, [compound.Target, compound.Value]),
        IInvocationOperation invocation => OfCall(invocation),
        _ => null
    };

    private static DelegateCombination? OfCall(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (!method.IsStatic || method.ContainingType?.SpecialType != SpecialType.System_Delegate ||
            method.Name is not ("Combine" or "Remove" or "RemoveAll"))
            return null;

        var parameters = method.Parameters;
        if (parameters.Length == 2 && parameters.All(parameter => parameter.Type.SpecialType == SpecialType.System_Delegate))
        {
            var operands = invocation.Arguments.Where(argument => argument.Parameter is not null)
                                     .OrderBy(argument => argument.Parameter!.Ordinal)
                                     .Select(argument => argument.Value)
                                     .ToArray();
            return operands.Length == 2 ? new(method.Name != "Combine", operands) : null;
        }

        if (method.Name != "Combine" || parameters.Length != 1 || invocation.Arguments.Length != 1)
            return null;

        var listed = invocation.Arguments[0];
        var value = WithoutConversions(listed.Value);
        switch (value)
        {
            case IArrayCreationOperation { Initializer: { } initializer } creation
                when initializer.ElementValues.All(element => element is not IArrayInitializerOperation):
                return new(false, initializer.ElementValues) { ListedIn = creation };
            case ICollectionExpressionOperation collection when !collection.Elements.Any(element => element is ISpreadOperation):
                return new(false, collection.Elements) { ListedIn = collection };
        }

        // Delegates not listed at the call are combined only out of an array whose cells the lowering can read; an existing span,
        // a spread or anything else stays the opaque call it is.
        return listed.ArgumentKind is not (ArgumentKind.ParamArray or ArgumentKind.ParamCollection) &&
               parameters[0].Type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Delegate } &&
               value is not ICollectionExpressionOperation
            ? new(false, [listed.Value]) { CombinesCells = true }
            : null;
    }

    private static bool IsDelegate(ITypeSymbol? type) => type?.TypeKind == TypeKind.Delegate;

    private static IOperation WithoutConversions(IOperation value) =>
        value is IConversionOperation conversion ? WithoutConversions(conversion.Operand) : value;
}
