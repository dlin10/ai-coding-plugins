using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class InjectionBindingTests
{
    private const string GATE_SINGLETON = "services.AddSingleton<Gate>();";

    [Fact]
    public void Readonly_field_assigned_from_a_constructor_parameter_binds()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate) { _gate = gate; }
            }
            """);

        var binding = Assert.Single(result.Bindings);
        Assert.Equal(("Holder", "Holder", "_gate", InjectionMemberKind.Field), (binding.Type, binding.DeclaringType, binding.Member, binding.MemberKind));
        Assert.Equal(("gate", 0, "Gate"), (binding.ConstructorParameter, binding.ConstructorParameterOrdinal, binding.ServiceType));
        Assert.Equal(DiResolutionKind.Bound, binding.Resolution.Kind);
        Assert.Equal("di:Gate@Singleton", binding.Resolution.Binding!.RegionId);
        Assert.Contains(binding.Evidence, evidence => evidence.Kind == "assignment" && evidence.Source.Path == "Case.cs");
        Assert.Contains(binding.Evidence, evidence => evidence.Kind == "registration" && evidence.Source.Path == "Case.cs");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Get_only_auto_property_assigned_from_a_constructor_parameter_binds()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                public Holder(Gate gate) => Gate = gate;
                public Gate Gate { get; }
            }
            """);

        var binding = Assert.Single(result.Bindings);
        Assert.Equal(("Gate", InjectionMemberKind.AutoProperty), (binding.Member, binding.MemberKind));
    }

    [Fact]
    public void Field_holding_a_new_object_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _initialized = new();
                private readonly Gate _constructed;
                public Holder(Gate gate) { _constructed = new Gate(); }
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Mutable_field_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private Gate _gate;
                public Holder(Gate gate) { _gate = gate; }
                public void Swap(Gate other) => _gate = other;
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Constructor_that_reassigns_the_parameter_before_storing_it_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate)
                {
                    gate = new Gate();
                    _gate = gate;
                }
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Parameter_passed_by_ref_in_the_constructor_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate)
                {
                    Replace(ref gate);
                    _gate = gate;
                }
                private static void Replace(ref Gate gate) => gate = new Gate();
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Field_passed_by_ref_in_the_constructor_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate)
                {
                    _gate = gate;
                    Replace(ref _gate);
                }
                private static void Replace(ref Gate gate) => gate = new Gate();
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Field_passed_by_in_from_another_member_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate) { _gate = gate; }
                public void Inspect() => Look(in _gate);
                private static void Look(in Gate gate) { }
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Readonly_field_passed_to_an_in_parameter_without_keyword_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate) { _gate = gate; }
                public void Inspect() => Look(_gate);
                private static void Look(in Gate gate) { }
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Field_assigned_from_two_different_parameters_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate first, Gate second, bool useFirst)
                {
                    if (useFirst) _gate = first; else _gate = second;
                }
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Primary_constructor_parameter_captured_by_a_member_binds()
    {
        var result = Bind("Holder", """
            public sealed class Holder(IGate unused, Gate gate)
            {
                public Gate Current() => gate;
            }
            """);

        var binding = Assert.Single(result.Bindings);
        Assert.Equal(("gate", InjectionMemberKind.PrimaryConstructorParameter, "gate", 1),
                     (binding.Member, binding.MemberKind, binding.ConstructorParameter, binding.ConstructorParameterOrdinal));
    }

    [Fact]
    public void Primary_constructor_parameter_passed_by_ref_by_a_member_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder(Gate gate)
            {
                public Gate Current() => gate;
                public void Reset() => Replace(ref gate);
                private static void Replace(ref Gate gate) => gate = new Gate();
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Primary_constructor_parameter_passed_to_an_in_parameter_without_keyword_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder(Gate gate)
            {
                public Gate Current() => gate;
                public void Inspect() => Look(gate);
                private static void Look(in Gate gate) { }
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Primary_constructor_parameter_assigned_by_a_member_is_not_bound()
    {
        var result = Bind("Holder", """
            public sealed class Holder(Gate gate)
            {
                public Gate Current() => gate;
                public void Reset() => gate = new Gate();
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Field_initialized_from_an_uncaptured_primary_constructor_parameter_binds_the_field()
    {
        var result = Bind("Holder", """
            public sealed class Holder(Gate gate)
            {
                private readonly Gate _gate = gate;
                public Gate Current() => _gate;
            }
            """);

        var binding = Assert.Single(result.Bindings);
        Assert.Equal(("_gate", InjectionMemberKind.Field, "gate"), (binding.Member, binding.MemberKind, binding.ConstructorParameter));
    }

    [Fact]
    public void Base_class_field_binds_through_a_pass_through_base_argument()
    {
        var result = Bind("DerivedController", BaseController + """
            public sealed class DerivedController : BaseController
            {
                public DerivedController(IGate other, Gate gate) : base(gate) { }
            }
            """);

        var binding = Assert.Single(result.Bindings);
        Assert.Equal(("DerivedController", "BaseController", "_gate"), (binding.Type, binding.DeclaringType, binding.Member));
        Assert.Equal(("gate", 1), (binding.ConstructorParameter, binding.ConstructorParameterOrdinal));
        Assert.Contains(binding.Evidence, evidence => evidence.Kind == "base-argument");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Base_class_field_reached_through_a_new_base_argument_is_not_bound()
    {
        var result = Bind("DerivedController", BaseController + """
            public sealed class DerivedController : BaseController
            {
                public DerivedController() : base(new Gate()) { }
            }
            """);

        Assert.Empty(result.Bindings);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RootDiscoveryDiagnosticCode.UnresolvedBinding, diagnostic.Code);
        Assert.Contains("BaseController._gate", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Base_class_field_reached_through_a_computed_base_argument_is_not_bound()
    {
        var result = Bind("DerivedController", BaseController + """
            public sealed class DerivedController : BaseController
            {
                public DerivedController(Gate? gate) : base(gate ?? new Gate()) { }
            }
            """);

        Assert.Empty(result.Bindings);
        Assert.Contains("BaseController._gate", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inherited_field_passed_to_an_in_parameter_in_the_derived_type_is_not_bound()
    {
        const string types = """
            public abstract class GateController
            {
                protected readonly Gate _gate;
                protected GateController(Gate gate) { _gate = gate; }
            }
            public sealed class ReadingController : GateController
            {
                public ReadingController(Gate gate) : base(gate) { }
                public Gate Current() => _gate;
            }
            public sealed class InspectingController : GateController
            {
                public InspectingController(Gate gate) : base(gate) { }
                public void Inspect() => Look(_gate);
                private static void Look(in Gate gate) { }
            }
            """;

        Assert.Equal("_gate", Assert.Single(Bind("ReadingController", types).Bindings).Member);
        Assert.Empty(Bind("InspectingController", types).Bindings);
    }

    [Fact]
    public void Inherited_field_assigned_in_the_derived_type_is_not_bound()
    {
        // C# rejects a plain assignment to a base type's readonly field in a derived type; a write through the
        // reference Unsafe.AsRef returns for a `ref readonly` argument is the assignment a derived type can make.
        var result = Bind("ResettingController", """
            public abstract class GateController
            {
                protected readonly Gate _gate;
                protected GateController(Gate gate) { _gate = gate; }
            }
            public sealed class ResettingController : GateController
            {
                public ResettingController(Gate gate) : base(gate) { }
                public void Reset() => System.Runtime.CompilerServices.Unsafe.AsRef(in _gate) = new Gate();
            }
            """);

        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Member_inherited_from_a_closed_generic_base_binds()
    {
        var result = Bind("GateController", """
            public abstract class BaseController<T> where T : class
            {
                protected readonly T _gate;
                protected BaseController(T gate) { _gate = gate; }
            }
            public sealed class GateController : BaseController<Gate>
            {
                public GateController(Gate gate) : base(gate) { }
            }
            """);

        var binding = Assert.Single(result.Bindings);
        Assert.Equal(("GateController", "BaseController<Gate>", "_gate", "gate"),
                     (binding.Type, binding.DeclaringType, binding.Member, binding.ConstructorParameter));
        Assert.Equal(("Gate", DiResolutionKind.Bound), (binding.ServiceType, binding.Resolution.Kind));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Implicit_parameterless_base_call_leaves_the_inherited_field_unbound()
    {
        var result = Bind("DerivedController", """
            public abstract class OptionalGateController
            {
                private readonly Gate? _gate;
                protected OptionalGateController() { }
                protected OptionalGateController(Gate gate) { _gate = gate; }
            }
            public sealed class DerivedController : OptionalGateController
            {
                public DerivedController(Gate gate) { }
            }
            """);

        Assert.Empty(result.Bindings);
        Assert.Contains("implicitly", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pass_through_chain_of_two_base_links_binds_to_the_most_derived_parameter()
    {
        var result = Bind("LeafController", BaseController + """
            public abstract class MiddleController : BaseController
            {
                protected MiddleController(Gate middle) : base(middle) { }
            }
            public sealed class LeafController(IGate other, Gate leaf) : MiddleController(leaf)
            {
                public IGate Other() => other;
            }
            """, "services.AddSingleton<Gate>(); services.AddSingleton<IGate, Gate>();");

        var inherited = Assert.Single(result.Bindings, binding => binding.Member == "_gate");
        Assert.Equal(("BaseController", "leaf", 1), (inherited.DeclaringType, inherited.ConstructorParameter, inherited.ConstructorParameterOrdinal));
        Assert.Single(result.Bindings, binding => binding.MemberKind == InjectionMemberKind.PrimaryConstructorParameter && binding.Member == "other");
    }

    [Fact]
    public void Registered_struct_service_is_not_bound()
    {
        var result = Bind("Holder", """
            public struct Clock { }
            public sealed class Holder
            {
                private readonly Clock _clock;
                public Holder(Clock clock) { _clock = clock; }
            }
            """, "services.AddSingleton(typeof(Clock));");

        Assert.Empty(result.Bindings);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RootDiscoveryDiagnosticCode.UnresolvedBinding, diagnostic.Code);
        Assert.Contains("value-type service", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_public_constructors_bind_nothing_and_name_the_count()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate) { _gate = gate; }
                public Holder() : this(new Gate()) { }
            }
            """);

        Assert.Empty(result.Bindings);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RootDiscoveryDiagnosticCode.UnresolvedBinding, diagnostic.Code);
        Assert.Contains("2 public constructors", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unregistered_parameter_type_binds_with_an_unregistered_resolution()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate) { _gate = gate; }
            }
            """, "");

        Assert.Equal(DiResolutionKind.Unregistered, Assert.Single(result.Bindings).Resolution.Kind);
    }

    [Fact]
    public void Ambiguous_parameter_type_binds_with_an_ambiguous_resolution()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate) { _gate = gate; }
            }
            """, "services.AddSingleton<Gate>(); services.AddScoped<Gate>();");

        var binding = Assert.Single(result.Bindings);
        Assert.Equal(DiResolutionKind.Ambiguous, binding.Resolution.Kind);
        Assert.Equal(2, binding.Evidence.Count(evidence => evidence.Kind == "registration"));
    }

    [Fact]
    public void Unresolved_registration_uncertainty_reaches_the_binding()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate) { _gate = gate; }
            }
            """, "services.AddSingleton<Gate>(); System.Type type = typeof(IGate); services.AddScoped(type);");

        var uncertainty = Assert.Single(Assert.Single(result.Bindings).Resolution.Binding!.Uncertainties);
        Assert.StartsWith("An unresolved registration at Case.cs:", uncertainty, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_parameter_not_stored_in_a_member_is_listed_with_its_resolution()
    {
        var result = Bind("Holder", """
            public sealed class Holder
            {
                private readonly string _name;
                public Holder(IGate other, Gate gate) { _name = gate.ToString()!; }
            }
            """, "services.AddSingleton<Gate>();");

        Assert.Empty(result.Bindings);
        Assert.Equal([("other", 0, "Fixture:IGate", DiResolutionKind.Unregistered), ("gate", 1, "Fixture:Gate", DiResolutionKind.Bound)],
                     result.ConstructorParameters.Select(parameter => (parameter.Name, parameter.Ordinal, parameter.TypeKey, parameter.Resolution.Kind)));
        Assert.Equal("di:Gate@Singleton", result.ConstructorParameters[1].Resolution.Binding!.RegionId);
    }

    [Fact]
    public void Discover_binds_every_concrete_source_class_of_the_scope()
    {
        var (compilations, index) = Compile("""
            public sealed class Holder
            {
                private readonly Gate _gate;
                public Holder(Gate gate) { _gate = gate; }
            }
            public static class Helpers { }
            """, GATE_SINGLETON);

        var results = InjectionBindings.Discover(compilations, index, @"C:\fixture", CancellationToken.None);

        Assert.Single(Assert.Single(results, result => result.Type == "Holder").Bindings);
        Assert.DoesNotContain(results, result => result.Type == "Helpers");
    }

    private const string BaseController = """
        public abstract class BaseController
        {
            private readonly Gate _gate;
            protected BaseController(Gate gate) { _gate = gate; }
        }

        """;

    private static TypeInjectionBindings Bind(string typeName, string types, string registrations = GATE_SINGLETON)
    {
        var (compilations, index) = Compile(types, registrations);
        var type = compilations[0].GetTypeByMetadataName(typeName) ?? throw new InvalidOperationException($"{typeName} was not found.");
        return InjectionBindings.Bind(type, compilations, index, @"C:\fixture", CancellationToken.None);
    }

    private static (IReadOnlyList<Microsoft.CodeAnalysis.Compilation> Compilations, DiIndex Index) Compile(string types, string registrations)
    {
        var source = $$"""
            using Microsoft.Extensions.DependencyInjection;
            public interface IGate { }
            public sealed class Gate : IGate { }
            {{types}}
            public static class Registrations
            {
                public static void Register(IServiceCollection services) { {{registrations}} }
            }
            """;
        var solution = FixtureSolution.Create(("Case.cs", source));
        var compilations = solution.Projects.Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()!).ToArray();
        return (compilations, DiIndexBuilder.Build("scope:Fixture", compilations, @"C:\fixture", CancellationToken.None));
    }
}
