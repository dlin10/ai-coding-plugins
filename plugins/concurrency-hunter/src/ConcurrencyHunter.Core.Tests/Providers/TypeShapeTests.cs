using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>What a type can carry, one shape per type, the first that applies (SPEC TD-034b, G-0).</summary>
public sealed class TypeShapeTests
{
    private const string SOURCE = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        public delegate void Notify(int value);
        public enum Mode { One, Two }
        public struct Point { public int X; public double Y; public Mode Mode; }
        public struct Nested { public Point A; public long B; }
        public struct Named { public string Name; public int Count; }
        public struct Carrier { public Point P; public object Item; }
        public ref struct Cursor { public int Position; public int Length; }
        public class Account { public int Balance; }
        public interface IStore { }

        public static unsafe class Shapes
        {
            public static int* Pointer;
            public static delegate*<int, void> FunctionPointer;
            public static Span<int> Span(Span<int> span) => span;
            public static Cursor Cursor(Cursor cursor) => cursor;
            public static Func<int, bool> Predicate;
            public static Action Action;
            public static Notify Notify;
            public static Task<int> TaskOfInt;
            public static ValueTask<Account> ValueTaskOfAccount;
            public static Task Task;
            public static ValueTask ValueTask;
            public static Point Point;
            public static Nested Nested;
            public static Named Named;
            public static Carrier Carrier;
            public static KeyValuePair<string, Account> Pair;
            public static Account Account;
            public static IStore Store;
            public static int[] Array;
            public static object Object;
            public static List<int> List;
            public static T Generic<T>(T value) => value;
        }
        """;

    private static readonly Lazy<CSharpCompilation> Compilation = new(() =>
        CSharpCompilation.Create("Fixture.Shapes", [CSharpSyntaxTree.ParseText(SOURCE, new CSharpParseOptions(LanguageVersion.Preview))],
                                 EmittedAssemblies.RuntimeReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)));

    [Fact]
    public void Fixture_compiles()
    {
        Assert.Empty(Compilation.Value.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void A_span_a_ref_struct_a_pointer_and_a_function_pointer_are_ref_like_or_pointer()
    {
        Assert.Equal(TypeShapeKind.RefLikeOrPointer, TypeShape.Of(MethodParameter("Span")));
        Assert.Equal(TypeShapeKind.RefLikeOrPointer, TypeShape.Of(MethodParameter("Cursor")));
        Assert.Equal(TypeShapeKind.RefLikeOrPointer, TypeShape.Of(Field("Pointer")));
        Assert.Equal(TypeShapeKind.RefLikeOrPointer, TypeShape.Of(Field("FunctionPointer")));
    }

    [Fact]
    public void A_ref_struct_of_plain_fields_is_ref_like_not_a_plain_struct()
    {
        var cursor = MethodParameter("Cursor");

        Assert.True(cursor.IsUnmanagedType);
        Assert.Equal(TypeShapeKind.RefLikeOrPointer, TypeShape.Of(cursor));
    }

    [Fact]
    public void A_delegate_type_is_a_delegate_not_a_reference()
    {
        Assert.Equal(TypeShapeKind.Delegate, TypeShape.Of(Field("Predicate")));
        Assert.Equal(TypeShapeKind.Delegate, TypeShape.Of(Field("Action")));
        Assert.Equal(TypeShapeKind.Delegate, TypeShape.Of(Field("Notify")));
        Assert.True(Field("Notify").IsReferenceType);
    }

    [Fact]
    public void Task_of_T_and_ValueTask_of_T_are_tasks_of_T()
    {
        Assert.Equal(TypeShapeKind.TaskOfT, TypeShape.Of(Field("TaskOfInt")));
        Assert.Equal(TypeShapeKind.TaskOfT, TypeShape.Of(Field("ValueTaskOfAccount")));
    }

    [Fact]
    public void ValueTask_of_T_is_a_task_of_T_not_a_struct_with_references()
    {
        var valueTask = Field("ValueTaskOfAccount");

        Assert.True(valueTask.IsValueType);
        Assert.False(valueTask.IsUnmanagedType);
        Assert.Equal(TypeShapeKind.TaskOfT, TypeShape.Of(valueTask));
    }

    [Fact]
    public void Task_and_ValueTask_are_tasks_not_references()
    {
        Assert.True(Field("Task").IsReferenceType);
        Assert.Equal(TypeShapeKind.Task, TypeShape.Of(Field("Task")));
        Assert.Equal(TypeShapeKind.Task, TypeShape.Of(Field("ValueTask")));
    }

    [Fact]
    public void Strings_primitives_enums_and_built_in_immutable_types_are_immutable()
    {
        var compilation = Compilation.Value;
        Assert.Equal(TypeShapeKind.Immutable, TypeShape.Of(compilation.GetSpecialType(SpecialType.System_String)));
        Assert.Equal(TypeShapeKind.Immutable, TypeShape.Of(compilation.GetSpecialType(SpecialType.System_Int32)));
        Assert.Equal(TypeShapeKind.Immutable, TypeShape.Of(compilation.GetSpecialType(SpecialType.System_Boolean)));
        Assert.Equal(TypeShapeKind.Immutable, TypeShape.Of(compilation.GetTypeByMetadataName("Mode")!));
        Assert.Equal(TypeShapeKind.Immutable, TypeShape.Of(compilation.GetTypeByMetadataName("System.DateTime")!));
        Assert.Equal(TypeShapeKind.Immutable, TypeShape.Of(compilation.GetTypeByMetadataName("System.Version")!));
    }

    [Fact]
    public void LibraryModels_IsImmutable_agrees_on_every_immutable_type()
    {
        var compilation = Compilation.Value;
        string[] names =
        [
            "System.Boolean", "System.Byte", "System.Char", "System.DateOnly", "System.DateTime", "System.DateTimeOffset", "System.Decimal",
            "System.Double", "System.Enum", "System.Exception", "System.InvalidOperationException", "System.Guid", "System.Int16",
            "System.Int32", "System.Int64", "System.SByte", "System.Single", "System.String", "System.StringComparer", "System.Text.Encoding",
            "System.TimeOnly", "System.TimeSpan", "System.Type", "System.UInt16", "System.UInt32", "System.UInt64", "System.Version"
        ];
        var types = names.Select(name => compilation.GetTypeByMetadataName(name))
                         .OfType<INamedTypeSymbol>()
                         .Append(compilation.GetTypeByMetadataName("System.Nullable`1")!.Construct(compilation.GetSpecialType(SpecialType.System_Int32)))
                         .Append(compilation.GetTypeByMetadataName("System.Collections.Generic.KeyValuePair`2")!
                                            .Construct(compilation.GetSpecialType(SpecialType.System_String), compilation.GetSpecialType(SpecialType.System_Int32)))
                         .ToArray();

        Assert.True(types.Length >= 25, $"{types.Length} types resolved");
        Assert.All(types, type =>
        {
            Assert.True(LibraryModels.BuiltIn.IsImmutable(type), $"{type} is not immutable by the built-in models");
            Assert.Equal(TypeShapeKind.Immutable, TypeShape.Of(type));
        });
        // Not immutable by the models, and not taken as one.
        Assert.False(LibraryModels.BuiltIn.IsImmutable(Field("Pair")));
        Assert.NotEqual(TypeShapeKind.Immutable, TypeShape.Of(Field("Pair")));
        Assert.NotEqual(TypeShapeKind.Immutable, TypeShape.Of(Field("Account")));
    }

    [Fact]
    public void A_struct_with_no_reference_at_any_depth_is_a_plain_struct()
    {
        Assert.Equal(TypeShapeKind.PlainStruct, TypeShape.Of(Field("Point")));
        Assert.Equal(TypeShapeKind.PlainStruct, TypeShape.Of(Field("Nested")));
    }

    [Fact]
    public void A_struct_holding_a_reference_at_any_depth_is_a_struct_with_references()
    {
        Assert.Equal(TypeShapeKind.StructWithReferences, TypeShape.Of(Field("Named")));
        Assert.Equal(TypeShapeKind.StructWithReferences, TypeShape.Of(Field("Carrier")));
        Assert.Equal(TypeShapeKind.StructWithReferences, TypeShape.Of(Field("Pair")));
    }

    [Fact]
    public void Classes_interfaces_arrays_object_and_type_parameters_are_references()
    {
        Assert.Equal(TypeShapeKind.Reference, TypeShape.Of(Field("Account")));
        Assert.Equal(TypeShapeKind.Reference, TypeShape.Of(Field("Store")));
        Assert.Equal(TypeShapeKind.Reference, TypeShape.Of(Field("Array")));
        Assert.Equal(TypeShapeKind.Reference, TypeShape.Of(Field("Object")));
        Assert.Equal(TypeShapeKind.Reference, TypeShape.Of(Field("List")));
        Assert.Equal(TypeShapeKind.Reference, TypeShape.Of(MethodParameter("Generic")));
    }

    private static ITypeSymbol Field(string name) =>
        Compilation.Value.GetTypeByMetadataName("Shapes")!.GetMembers(name).OfType<IFieldSymbol>().Single().Type;

    private static ITypeSymbol MethodParameter(string name) =>
        Compilation.Value.GetTypeByMetadataName("Shapes")!.GetMembers(name).OfType<IMethodSymbol>().Single().Parameters[0].Type;
}
