using System.Collections.Concurrent;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The recipe's ordered sources through every route that asks it for a library value.</summary>
public sealed class ValueSourceMatrixTests
{
    private enum Source
    {
        PublicCtor, Factory, StaticField, StaticProperty, InternalCtor, Implementation, None,
        PublicCtorAndFactory, FactoryAndStaticField, StaticFieldAndStaticProperty, StaticPropertyAndInternalCtor,
        PublicCtorAndInternalCtor, StaticPropertyAndImplementation, PublicCtorUnbuiltAndFactory, FactoryUnbuiltAndStaticField,
        PublicCtorUnbuiltAndInternalCtor, InternalCtorUnbuilt, FactoryUnbuiltAndImplementation, PublicCtorPartlyBuiltAndStaticField,
        TwoPublicCtorsFirstUnbuilt, TwoFactoriesFirstUnbuilt, TwoInternalCtorsFirstUnbuilt, TwoImplementationsFirstUnbuilt,
        StaticPropertyBeforeStaticField, StaticFieldOfBaseType, InstanceFieldOfType, InstancePropertyOfType,
        StaticAutoPropertyWithPrivateGetter, StaticPropertyWithPrivateGetterBody, PrivateStaticFieldOpened, PrivateCtorOnly,
        InstancePropertyBeforeStaticField
    }
    private enum Shape { SealedClass, StructWithReferences, NonDerivableAbstract }
    private enum Place
    {
        Receiver, Argument, NestedConstructorArgument, NestedFactoryArgument, PairContent, TaskContent, BaseConstructorArgument,
        Seed, TriggerArgument, LambdaReturn, WitnessReturn, WitnessOut
    }
    private sealed record Cell(Source Source, Shape Shape, Place Place);

    private static readonly Cell[] Cells = (from source in Enum.GetValues<Source>()
                                            from shape in Enum.GetValues<Shape>()
                                            from place in Enum.GetValues<Place>()
                                            where Allowed(source, shape, place)
                                            select new Cell(source, shape, place)).ToArray();

    [Fact]
    public void Every_cell_gets_its_expected_value()
    {
        var wrong = new ConcurrentQueue<string>();
        Parallel.ForEach(Cells, CellParallelism, cell =>
        {
            try { Check(cell); }
            catch (Exception error) { wrong.Enqueue($"{cell}: {error.Message}"); }
        });
        Assert.True(wrong.IsEmpty, $"{wrong.Count} of {Cells.Length} cells:\n" + string.Join('\n', wrong.Take(30)));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<Source>(), Cells.Select(cell => cell.Source).Distinct().Order());
        Assert.Equal(Enum.GetValues<Shape>(), Cells.Select(cell => cell.Shape).Distinct().Order());
        Assert.Equal(Enum.GetValues<Place>(), Cells.Select(cell => cell.Place).Distinct().Order());
    }

    private static bool Allowed(Source source, Shape shape, Place place)
    {
        // Recipe deliberately defaults structs before consulting sources; returns and witnesses consult sources.
        if (shape == Shape.StructWithReferences && place is not (Place.LambdaReturn or Place.WitnessReturn or Place.WitnessOut))
            return false;
        // Abstract witnesses use seeds; the remaining Value routes are exercised with a sealed class.
        if (shape == Shape.NonDerivableAbstract && place is not (Place.Receiver or Place.Argument or Place.NestedConstructorArgument or
                                                                 Place.NestedFactoryArgument or Place.LambdaReturn))
            return false;
        // A constructor cannot instantiate an abstract type.
        if (shape == Shape.NonDerivableAbstract && source is Source.PublicCtor or Source.InternalCtor or Source.PrivateCtorOnly or
            Source.PublicCtorAndFactory or Source.StaticPropertyAndInternalCtor or Source.PublicCtorAndInternalCtor or
            Source.PublicCtorUnbuiltAndFactory or Source.PublicCtorUnbuiltAndInternalCtor or Source.InternalCtorUnbuilt or
            Source.PublicCtorPartlyBuiltAndStaticField or Source.TwoPublicCtorsFirstUnbuilt or Source.TwoInternalCtorsFirstUnbuilt)
            return false;
        // Only abstract targets need the implementation fallback; structs cannot have a base class.
        if (source is Source.Implementation or Source.TwoImplementationsFirstUnbuilt or Source.StaticPropertyAndImplementation or
                      Source.FactoryUnbuiltAndImplementation && shape != Shape.NonDerivableAbstract)
            return false;
        // A struct has no base class and cannot contain an instance field of its own type (CS0523).
        if (source is Source.StaticFieldOfBaseType or Source.InstanceFieldOfType && shape == Shape.StructWithReferences)
            return false;
        return true;
    }

    private static string? Expected(Source source) => source switch
    {
        Source.PublicCtor or Source.PublicCtorAndFactory or Source.PublicCtorAndInternalCtor => "new global::Lib.V(\"s\")",
        Source.Factory or Source.FactoryAndStaticField or Source.PublicCtorUnbuiltAndFactory => "global::Lib.V.FromFactory()",
        Source.StaticField or Source.StaticFieldAndStaticProperty or Source.FactoryUnbuiltAndStaticField or
        Source.PublicCtorPartlyBuiltAndStaticField or Source.StaticPropertyBeforeStaticField or
        Source.InstancePropertyBeforeStaticField => "global::Lib.V.SharedField",
        Source.StaticProperty or Source.StaticPropertyAndInternalCtor or Source.StaticPropertyAndImplementation => "global::Lib.V.SharedProperty",
        Source.InternalCtor or Source.PublicCtorUnbuiltAndInternalCtor => "new global::Lib.V(default(global::System.Int32))",
        Source.Implementation or Source.FactoryUnbuiltAndImplementation => "new global::Lib.Implementation()",
        Source.TwoImplementationsFirstUnbuilt => "new global::Lib.Implementation(default(global::System.Int32), default(global::System.Int32))",
        Source.TwoPublicCtorsFirstUnbuilt or Source.TwoInternalCtorsFirstUnbuilt => "new global::Lib.V(default(global::System.Int32), default(global::System.Int32))",
        Source.TwoFactoriesFirstUnbuilt => "global::Lib.V.SecondFactory(default(global::System.Int32), default(global::System.Int32))",
        _ => null
    };

    private static void Check(Cell cell)
    {
        var library = Compile(LibrarySource(cell));
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        Assert.Empty(library.ExternMembers);
        var type = library.Compilation.GetTypeByMetadataName("Lib.V")!;
        if (cell.Shape == Shape.NonDerivableAbstract)
        {
            Assert.False(Derivation.CanDerive(type));
            Assert.Equal(ParameterKind.RecipeValue, ParameterKinds.Of(type));
        }
        if (cell.Place == Place.Seed)
        {
            Assert.False(SeedableFields.Seed(type));
            Assert.True(SeedableFields.Path(type));
        }
        if (cell.Source == Source.StaticAutoPropertyWithPrivateGetter)
        {
            var property = Assert.IsAssignableFrom<IPropertySymbol>(Assert.Single(type.GetMembers("Hidden")));
            Assert.Equal(Accessibility.Public, property.GetMethod!.DeclaredAccessibility);
            Assert.Contains("P:Lib.V.Hidden", library.OpenedFields);
        }
        if (cell.Source == Source.PrivateStaticFieldOpened)
        {
            var field = Assert.IsAssignableFrom<IFieldSymbol>(Assert.Single(type.GetMembers("Hidden")));
            Assert.Equal(Accessibility.Internal, field.DeclaredAccessibility);
            Assert.Contains("F:Lib.V.Hidden", library.OpenedFields);
        }
        var targetType = cell.Place == Place.Receiver ? type : library.Compilation.GetTypeByMetadataName("Lib.Api")!;
        var member = targetType.GetMembers("Run").OfType<IMethodSymbol>().Single();
        var synthesis = DriverSynthesizer.Synthesize(library.Compilation, member, library.ExternMembers, library.OpenedFields, CancellationToken.None);
        var expected = Expected(cell.Source);
        var optional = cell.Place is Place.Seed or Place.TriggerArgument or Place.WitnessReturn or Place.WitnessOut;
        if (expected is null && !optional)
        {
            Assert.Null(synthesis.Driver);
            Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
            Assert.Contains("only a default could fill", synthesis.Detail);
            return;
        }
        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        var driver = synthesis.Driver;
        Assert.Empty(driver.DefaultedValues);
        Assert.Empty(driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        // Synthesize also checks that the call binds to the requested declaration.
        if (expected is not null)
            Assert.Contains(expected, driver.Source);
        if (cell.Place is Place.WitnessReturn or Place.WitnessOut)
        {
            var witness = driver.Compilation.GetTypeByMetadataName("Probe_user")!.GetMembers().OfType<IMethodSymbol>()
                                .Single(method => method.Name.EndsWith("Make", StringComparison.Ordinal));
            Assert.Equal(expected is null, witness.IsExtern);
        }
        if (cell.Place == Place.TriggerArgument)
            Assert.Equal(expected is not null, driver.Triggers.Any(trigger => trigger.Member.Contains("Fire", StringComparison.Ordinal)));
        if (cell.Place == Place.Seed)
            Assert.Equal(expected is null, driver.Unseeded.Any(path => path.Contains("Values", StringComparison.Ordinal)));
        Assert.DoesNotContain("L_value_1_", driver.Source);
    }

    private static string LibrarySource(Cell cell)
    {
        var source = cell.Source;
        var structure = cell.Shape == Shape.StructWithReferences;
        var abstractType = cell.Shape == Shape.NonDerivableAbstract;
        var declarations = new List<string>();
        var constructors = new List<string>();
        var hasPublic = source is Source.PublicCtor or Source.PublicCtorAndFactory or Source.PublicCtorAndInternalCtor;
        var hasInternal = source is Source.InternalCtor or Source.PublicCtorAndInternalCtor or Source.StaticPropertyAndInternalCtor or Source.PublicCtorUnbuiltAndInternalCtor;
        var publicUnbuilt = source is Source.PublicCtorUnbuiltAndFactory or Source.PublicCtorUnbuiltAndInternalCtor or Source.TwoPublicCtorsFirstUnbuilt;
        var internalUnbuilt = source is Source.InternalCtorUnbuilt or Source.TwoInternalCtorsFirstUnbuilt;
        if (hasPublic) constructors.Add("public V(string tag) { Slot = null; }");
        if (hasInternal) constructors.Add("internal V(int tag) { Slot = null; }");
        if (publicUnbuilt) constructors.Add("public V(Unbuildable missing) { Slot = null; }");
        if (internalUnbuilt) constructors.Add("internal V(Unbuildable missing) { Slot = null; }");
        if (source == Source.PublicCtorPartlyBuiltAndStaticField) constructors.Add("public V(Action probe, Unbuildable missing) { Slot = probe; }");
        if (source is Source.TwoPublicCtorsFirstUnbuilt or Source.TwoInternalCtorsFirstUnbuilt)
            constructors.Add($"{(source == Source.TwoPublicCtorsFirstUnbuilt ? "public" : "internal")} V(int a, int b) {{ Slot = null; }}");
        if (!abstractType && !structure) constructors.Add("private V() { }");
        if (abstractType) constructors.Add("protected V() { } internal abstract void Block();");
        declarations.AddRange(constructors);
        if (source is Source.Factory or Source.PublicCtorAndFactory or Source.FactoryAndStaticField or Source.PublicCtorUnbuiltAndFactory)
            declarations.Add("public static V FromFactory() => throw null;");
        if (source is Source.FactoryUnbuiltAndStaticField or Source.FactoryUnbuiltAndImplementation or Source.TwoFactoriesFirstUnbuilt)
            declarations.Add("public static V FirstFactory(Unbuildable missing) => throw null;");
        if (source == Source.TwoFactoriesFirstUnbuilt)
            declarations.Add("public static V SecondFactory(int a, int b) => throw null;");
        if (source == Source.InstancePropertyBeforeStaticField) declarations.Add("public V Instance => this;");
        if (source == Source.StaticPropertyBeforeStaticField) declarations.Add("public static V SharedProperty => throw null;");
        if (source is Source.StaticField or Source.FactoryAndStaticField or Source.StaticFieldAndStaticProperty or Source.FactoryUnbuiltAndStaticField or
                      Source.PublicCtorPartlyBuiltAndStaticField or Source.StaticPropertyBeforeStaticField or Source.InstancePropertyBeforeStaticField)
            declarations.Add("public static V SharedField;");
        if (source is Source.StaticProperty or Source.StaticFieldAndStaticProperty or Source.StaticPropertyAndInternalCtor or Source.StaticPropertyAndImplementation)
            declarations.Add("public static V SharedProperty => throw null;");
        if (source == Source.StaticFieldOfBaseType) declarations.Add("public static Base Shared;");
        if (source == Source.InstanceFieldOfType) declarations.Add("public V Instance;");
        if (source == Source.InstancePropertyOfType) declarations.Add("public V Instance => this;");
        if (source == Source.StaticAutoPropertyWithPrivateGetter) declarations.Add("public static V Hidden { private get; set; }");
        if (source == Source.StaticPropertyWithPrivateGetterBody) declarations.Add("public static V Hidden { private get => throw null; set { } }");
        if (source == Source.PrivateStaticFieldOpened) declarations.Add("private static V Hidden;");
        var kind = structure ? "struct" : abstractType ? "abstract class" : "sealed class";
        var bases = !structure && source == Source.StaticFieldOfBaseType ? " : Base" : "";
        var value = $"public {kind} V{bases} {{ public Action Slot; {string.Join(" ", declarations)} public void Run(Action done) {{ }} }}";
        var implementations = "";
        if (source is Source.Implementation or Source.StaticPropertyAndImplementation or Source.FactoryUnbuiltAndImplementation or Source.TwoImplementationsFirstUnbuilt)
        {
            if (source == Source.TwoImplementationsFirstUnbuilt)
                implementations += "public sealed class FirstImplementation : V { public FirstImplementation(Unbuildable missing) { } internal override void Block() { } } ";
            // The rejected implementation has fewer arguments, and therefore precedes the successful one.
            implementations += source == Source.TwoImplementationsFirstUnbuilt
                ? "public sealed class Implementation : V { public Implementation(int a = 0, int b = 0) { } internal override void Block() { } }"
                : "public sealed class Implementation : V { public Implementation() { } internal override void Block() { } }";
        }
        var route = cell.Place switch
        {
            Place.Receiver => "public static class Api { }",
            Place.Argument => "public static class Api { public static void Run(V value) { } }",
            Place.NestedConstructorArgument => "public sealed class Wrap { public Wrap(V value) { } } public static class Api { public static void Run(Wrap value) { } }",
            Place.NestedFactoryArgument => "public sealed class Wrap { private Wrap() { } public static Wrap Create(V value) => throw null; } public static class Api { public static void Run(Wrap value) { } }",
            Place.PairContent => "public static class Api { public static void Run(KeyValuePair<string, V> value) { } }",
            Place.TaskContent => "public static class Api { public static void Run(Task<V> value) { } }",
            Place.BaseConstructorArgument => "public abstract class User { protected User(V value) { } } public static class Api { public static void Run(User user) { } }",
            Place.Seed => "public sealed class Holder { public V[] Values; } public static class Api { public static void Run(Holder holder) { } }",
            Place.TriggerArgument => "public sealed class Holder { public void Fire(V value) { } } public static class Api { public static Holder Run(Action done) => new Holder(); }",
            Place.LambdaReturn => "public static class Api { public static void Run(Func<V> make) { } }",
            Place.WitnessReturn => "public interface User { V Make(); } public static class Api { public static void Run(User user) { } }",
            _ => "public interface User { void Make(out V value); } public static class Api { public static void Run(User user) { } }"
        };
        return "using System; using System.Collections.Generic; using System.Threading.Tasks; namespace Lib { " +
               "public class Base { } public sealed class Unbuildable { private Unbuildable() { } } " + value + implementations + route + " }";
    }
}
