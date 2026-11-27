using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Frontend;

/// <summary>
/// The C# 14 instance compound assignment and increment operators (<c>void operator +=(T)</c>, <c>void operator ++()</c>), the one answer
/// every reading of <c>x op= y</c>, <c>++x</c> and <c>x++</c> asks for: whether it calls an instance operator, and whether it then
/// assigns to its target (C# 14 feature specification, "User Defined Compound Assignment Operators"). On a target of a type known to be
/// a reference type, C# evaluates the target to get an object, calls the operator on that object and hands the object back; nothing is
/// assigned. On any other target — a struct, a type parameter not constrained to a class — whose result is not used, it calls the
/// operator on the target itself; where the result is used, it reads the target into a copy, calls the operator on the copy, assigns the
/// copy to the target and hands it back. Whether the result is used is the operation's type and where its value goes
/// (<see cref="IsUsed"/>); an operator Roslyn picks that is static keeps the long form <c>x = x op y</c>.
/// </summary>
internal static class InstanceOperators
{
    /// <summary>The instance operator a compound assignment or increment calls, whoever declares it — one only metadata declares also
    /// updates its object, so no computation stands for it; <c>null</c> for a static operator, for a predefined one and for any other
    /// operation.</summary>
    /// <param name="operation">A compound assignment, an increment or a decrement, or any other operation.</param>
    public static IMethodSymbol? Of(IOperation operation) => operation switch
    {
        ICompoundAssignmentOperation { OperatorMethod: { MethodKind: MethodKind.UserDefinedOperator, IsStatic: false } method } => method,
        IIncrementOrDecrementOperation { OperatorMethod: { MethodKind: MethodKind.UserDefinedOperator, IsStatic: false } method } => method,
        _ => null
    };

    /// <summary>The instance operator an operation calls on its target itself, assigning nothing to it: <see cref="Of"/>, unless the
    /// target is not known to be a reference type and the result is used, where C# assigns the copy it called the operator on.</summary>
    /// <param name="operation">A compound assignment, an increment or a decrement, or any other operation.</param>
    public static IMethodSymbol? InPlace(IOperation operation)
    {
        var target = operation switch
        {
            ICompoundAssignmentOperation compound => compound.Target,
            IIncrementOrDecrementOperation increment => increment.Target,
            _ => null
        };
        var storesBack = target?.Type is not { IsReferenceType: true } && IsUsed(operation);
        return storesBack ? null : Of(operation);
    }

    /// <summary>Whether the value of an operation is used. Roslyn types an operation whose value it already knows is unused
    /// <c>void</c>, but not the assignment a null-conditional access <c>a?.b op= y</c> makes, which keeps its target's type though the
    /// statement discards the value and C# calls the operator in place: that value goes, through each conditional access whose
    /// <c>WhenNotNull</c> it is, into an expression statement — in the operation tree, and in the control-flow graph, where the
    /// statement holds the assignment itself.</summary>
    /// <param name="operation">The operation.</param>
    private static bool IsUsed(IOperation operation)
    {
        if (operation.Type is null or { SpecialType: SpecialType.System_Void })
            return false;
        var value = operation;
        while (value.Parent is IConditionalAccessOperation conditional && conditional.WhenNotNull == value)
        {
            WorkCancellation.Check();
            value = conditional;
        }
        return value.Parent is not IExpressionStatementOperation;
    }
}
