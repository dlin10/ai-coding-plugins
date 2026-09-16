using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class DiIndexTests
{
    private const string HOSTED = DiIndex.HOSTED_SERVICE_KEY;

    [Fact]
    public void Generic_self_registration_binds_the_type_to_itself()
    {
        var index = Index("services.AddSingleton<Gate>();");

        var binding = Bound(index, Key("Gate"));
        Assert.Equal("Gate", binding.ImplementationType);
        Assert.Equal(DiLifetime.Singleton, binding.Lifetime);
        Assert.Equal("di:Gate@Singleton", binding.RegionId);
        Assert.Empty(binding.Uncertainties);
        Assert.Equal("Case.cs", Assert.Single(binding.Sources).Path);
    }

    [Fact]
    public void Generic_service_and_implementation_registration_binds_the_service()
    {
        var index = Index("services.AddScoped<IGate, Gate>();");

        var binding = Bound(index, Key("IGate"));
        Assert.Equal("Gate", binding.ImplementationType);
        Assert.Equal(DiLifetime.Scoped, binding.Lifetime);
        Assert.Equal("di:Gate@Scoped", binding.RegionId);
        Assert.Equal(DiResolutionKind.Unregistered, index.Resolve(Key("Gate")).Kind);
    }

    [Fact]
    public void Closed_typeof_forms_register_like_the_generic_forms()
    {
        var index = Index("""
            services.AddTransient(typeof(Gate));
            services.AddSingleton(typeof(IGate), typeof(OtherGate));
            services.AddScoped(typeof(Repository<Gate>));
            """);

        Assert.Equal(("Gate", DiLifetime.Transient), Target(Bound(index, Key("Gate"))));
        Assert.Equal(("OtherGate", DiLifetime.Singleton), Target(Bound(index, Key("IGate"))));
        Assert.Equal(("Repository<Gate>", DiLifetime.Scoped), Target(Bound(index, Key("Repository<Fixture:Gate>"))));
    }

    [Fact]
    public void Try_add_forms_register_service_and_implementation()
    {
        var index = Index("""
            services.TryAddSingleton<Gate>();
            services.TryAddScoped<IGate, OtherGate>();
            services.TryAddTransient(typeof(Repository<Gate>));
            """);

        Assert.Equal(("Gate", DiLifetime.Singleton), Target(Bound(index, Key("Gate"))));
        Assert.Equal(("OtherGate", DiLifetime.Scoped), Target(Bound(index, Key("IGate"))));
        Assert.Equal(("Repository<Gate>", DiLifetime.Transient), Target(Bound(index, Key("Repository<Fixture:Gate>"))));
    }

    [Fact]
    public void Generic_factory_forms_bind_and_non_generic_stay_unsupported()
    {
        var index = Index("""
            services.AddSingleton<Gate>(_ => new Gate());
            services.AddScoped(typeof(IGate), _ => new OtherGate());
            services.TryAddTransient<OtherGate>(_ => new OtherGate());
            """);

        Assert.Equal(("Gate", DiLifetime.Singleton), Target(Bound(index, Key("Gate"))));
        Assert.Equal(DiResolutionKind.Unsupported, index.Resolve(Key("IGate")).Kind);
        Assert.Equal(("OtherGate", DiLifetime.Transient), Target(Bound(index, Key("OtherGate"))));
        Assert.Equal(new string?[] { null, "factory", null }, index.Registrations.Select(registration => registration.UnsupportedReason));
    }

    [Fact]
    public void Generic_instance_form_binds_and_non_generic_stays_unsupported()
    {
        var index = Index("""
            services.AddSingleton(new Gate());
            services.AddSingleton(typeof(IGate), new OtherGate());
            """);

        Assert.Equal(new string?[] { null, "instance" }, index.Registrations.Select(registration => registration.UnsupportedReason));
        Assert.Equal(("Gate", DiLifetime.Singleton), Target(Bound(index, Key("Gate"))));
        Assert.Equal(DiResolutionKind.Unsupported, index.Resolve(Key("IGate")).Kind);
    }

    [Fact]
    public void Keyed_forms_are_unsupported()
    {
        var index = Index("""
            services.AddKeyedSingleton<Gate>("primary");
            services.AddKeyedScoped(typeof(IGate), "secondary", typeof(OtherGate));
            """);

        Assert.All(index.Registrations, registration => Assert.Equal("keyed", registration.UnsupportedReason));
        Assert.Equal(DiResolutionKind.Unsupported, index.Resolve(Key("Gate")).Kind);
        Assert.Equal(DiResolutionKind.Unsupported, index.Resolve(Key("IGate")).Kind);
    }

    [Fact]
    public void Open_generic_type_form_is_unsupported()
    {
        var index = Index("services.AddSingleton(typeof(Repository<>));");

        var registration = Assert.Single(index.Registrations);
        Assert.Equal("open generic Type", registration.UnsupportedReason);
        Assert.NotNull(registration.ServiceType);
        Assert.Empty(index.UnresolvedRegistrations);
    }

    [Fact]
    public void Type_parameter_forms_are_unsupported()
    {
        var index = Index(
            "Register<Gate>(services);",
            "public static void Register<T>(IServiceCollection services) where T : class { services.AddSingleton<T>(); services.AddScoped(typeof(Repository<T>)); }");

        Assert.Equal(["type parameter", "type parameter"], index.Registrations.Select(registration => registration.UnsupportedReason));
        Assert.Equal(DiResolutionKind.Unsupported, index.Resolve(Key("T")).Kind);
    }

    [Fact]
    public void Non_typeof_service_type_makes_every_bound_answer_uncertain()
    {
        const string body = """
            services.AddSingleton<Gate>();
            var type = typeof(IGate);
            services.AddScoped(type);
            """;
        var index = Index(body);

        var unresolved = Assert.Single(index.UnresolvedRegistrations);
        Assert.Null(unresolved.ServiceType);
        Assert.Equal("non-typeof Type", unresolved.UnsupportedReason);
        var line = SourceLine(body, "services.AddScoped(type);");
        Assert.Equal([$"An unresolved registration at Case.cs:{line} may change this binding."], Bound(index, Key("Gate")).Uncertainties);
    }

    [Fact]
    public void Non_typeof_implementation_type_is_unsupported_for_its_service_only()
    {
        var index = Index("""
            services.AddSingleton<Gate>();
            Type implementation = typeof(OtherGate);
            services.AddSingleton(typeof(IGate), implementation);
            """);

        Assert.Equal(DiResolutionKind.Unsupported, index.Resolve(Key("IGate")).Kind);
        Assert.Empty(index.UnresolvedRegistrations);
        Assert.Empty(Bound(index, Key("Gate")).Uncertainties);
    }

    [Fact]
    public void Registration_in_an_extension_method_nothing_calls_is_indexed()
    {
        var index = IndexOf("""
            public static class UnusedRegistrations
            {
                public static IServiceCollection AddGate(this IServiceCollection services) => services.AddSingleton<IGate, Gate>();
            }
            """);

        Assert.Equal("Gate", Bound(index, Key("IGate")).ImplementationType);
    }

    [Fact]
    public void Out_of_range_dependency_injection_version_is_a_diagnostic_and_skipped()
    {
        var index = IndexOf(Wrap("services.AddSingleton<Gate>(); services.AddScoped<IGate, OtherGate>();"),
                            new FixtureOptions
                            {
                                StubVersions = new Dictionary<string, int> { [StubAssemblies.DEPENDENCY_INJECTION_ABSTRACTIONS] = 7 }
                            });

        Assert.Empty(index.Registrations);
        var diagnostic = Assert.Single(index.Diagnostics);
        Assert.Equal(RootDiscoveryDiagnosticCode.UnsupportedAssemblyVersion, diagnostic.Code);
        Assert.Equal(StubAssemblies.DEPENDENCY_INJECTION_ABSTRACTIONS, diagnostic.Subject);
        Assert.Contains("7.0.0.0", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(DiResolutionKind.Unregistered, index.Resolve(Key("Gate")).Kind);
    }

    [Fact]
    public void Unregistered_type_resolves_as_unregistered()
    {
        var index = Index("services.AddSingleton<Gate>();");

        var resolution = index.Resolve(Key("OtherGate"));
        Assert.Equal(DiResolutionKind.Unregistered, resolution.Kind);
        Assert.Null(resolution.Binding);
    }

    [Fact]
    public void Registrations_that_disagree_on_lifetime_or_implementation_are_ambiguous()
    {
        var index = Index("""
            services.AddSingleton<Gate>();
            services.AddScoped<Gate>();
            services.AddSingleton<IGate, Gate>();
            services.AddSingleton<IGate, OtherGate>();
            """);

        Assert.Equal(DiResolutionKind.Ambiguous, index.Resolve(Key("Gate")).Kind);
        Assert.Equal(DiResolutionKind.Ambiguous, index.Resolve(Key("IGate")).Kind);
        Assert.Equal(2, index.Resolve(Key("IGate")).Registrations.Count);
    }

    [Fact]
    public void Identical_duplicate_registrations_bind_once_with_every_source()
    {
        var index = Index("""
            services.AddSingleton<Gate>();
            services.AddSingleton(typeof(Gate), typeof(Gate));
            """);

        var binding = Bound(index, Key("Gate"));
        Assert.Equal(2, binding.Sources.Count);
        Assert.NotEqual(binding.Sources[0].StartLine, binding.Sources[1].StartLine);
    }

    [Fact]
    public void Any_unsupported_registration_of_a_type_makes_it_unsupported()
    {
        var index = Index("""
            services.AddSingleton<Gate>();
            services.AddSingleton(typeof(Gate), _ => new Gate());
            """);

        Assert.Equal(DiResolutionKind.Unsupported, index.Resolve(Key("Gate")).Kind);
    }

    [Fact]
    public void Unsupported_registration_is_a_diagnostic_with_its_reason_and_location()
    {
        const string body = """
            services.AddSingleton<Gate>();
            services.AddScoped(typeof(IGate), _ => new OtherGate());
            """;
        var index = Index(body);

        var line = SourceLine(body, "services.AddScoped(typeof(IGate)");
        var diagnostic = Assert.Single(index.Diagnostics);
        Assert.Equal(RootDiscoveryDiagnosticCode.UnsupportedPattern, diagnostic.Code);
        Assert.Equal("IGate", diagnostic.Subject);
        Assert.Contains("factory", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains($"Case.cs:{line}", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(line, diagnostic.Source!.StartLine);
    }

    [Fact]
    public void Ambiguous_registration_is_a_diagnostic_naming_every_location()
    {
        const string body = """
            services.AddSingleton<Gate>();
            services.AddScoped<Gate>();
            services.AddSingleton(typeof(Gate));
            services.AddSingleton<IOtherGate, Gate>();
            """;
        var index = Index(body);

        var diagnostic = Assert.Single(index.Diagnostics);
        Assert.Equal(RootDiscoveryDiagnosticCode.UnresolvedBinding, diagnostic.Code);
        Assert.Equal("Gate", diagnostic.Subject);
        Assert.Contains("Gate@Singleton", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Gate@Scoped", diagnostic.Message, StringComparison.Ordinal);
        var lines = body.Split('\n').Take(3).Select(text => SourceLine(body, text.Trim())).Distinct().ToArray();
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Contains($"Case.cs:{line}", diagnostic.Message, StringComparison.Ordinal));
        Assert.Equal(index.Diagnostics, Index(body).Diagnostics);
    }

    [Fact]
    public void Two_services_on_one_implementation_share_the_region_but_keep_their_service_types()
    {
        var index = Index("""
            services.AddSingleton<IGate, Gate>();
            services.AddSingleton<IOtherGate, Gate>();
            """);

        var first = Bound(index, Key("IGate"));
        var second = Bound(index, Key("IOtherGate"));
        Assert.Equal("di:Gate@Singleton", first.RegionId);
        Assert.Equal(first.RegionId, second.RegionId);
        Assert.NotEqual(first.ServiceType, second.ServiceType);
    }

    [Fact]
    public void Add_hosted_service_registers_one_singleton_hosted_service()
    {
        var index = Index("services.AddHostedService<Worker>();");

        var hosted = Assert.Single(index.HostedServices);
        Assert.Equal("Worker", hosted.ImplementationType);
        Assert.Equal(HostedServiceInstanceCount.One, hosted.InstanceCount);
        Assert.Equal(("Worker", DiLifetime.Singleton), Target(Bound(index, HOSTED)));
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public void Repeated_add_hosted_service_is_one_instance()
    {
        var index = Index("""
            services.AddHostedService<Worker>();
            services.AddHostedService<Worker>();
            """);

        var hosted = Assert.Single(index.HostedServices);
        Assert.Equal(HostedServiceInstanceCount.One, hosted.InstanceCount);
        Assert.Equal(2, hosted.Registrations.Count);
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public void One_singleton_hosted_service_registration_is_one_instance()
    {
        var index = Index("services.AddSingleton<IHostedService, Worker>();");

        var hosted = Assert.Single(index.HostedServices);
        Assert.Equal("Worker", hosted.ImplementationType);
        Assert.Equal(HostedServiceInstanceCount.One, hosted.InstanceCount);
    }

    [Fact]
    public void Repeated_singleton_hosted_service_registration_is_unknown_with_a_diagnostic()
    {
        var index = Index("""
            services.AddSingleton<IHostedService, Worker>();
            services.AddSingleton<IHostedService, Worker>();
            """);

        AssertUnknownHostedCount(index);
    }

    [Theory]
    [InlineData("services.AddHostedService<Worker>(); services.AddSingleton<IHostedService, Worker>();")]
    [InlineData("services.AddSingleton<IHostedService, Worker>(); services.AddHostedService<Worker>();")]
    public void Add_hosted_service_with_a_singleton_hosted_service_is_unknown_in_either_order(string body)
    {
        AssertUnknownHostedCount(Index(body));
    }

    [Fact]
    public void Hosted_service_factory_is_unsupported_and_not_counted()
    {
        var index = Index("services.AddHostedService(_ => new Worker());");

        Assert.Equal("factory", Assert.Single(index.Registrations).UnsupportedReason);
        Assert.Empty(index.HostedServices);
    }

    [Fact]
    public void Out_of_range_hosting_version_skips_hosted_services_with_a_diagnostic()
    {
        var index = IndexOf(Wrap("services.AddHostedService<Worker>(); services.AddSingleton<Gate>();"),
                            new FixtureOptions
                            {
                                StubVersions = new Dictionary<string, int> { [StubAssemblies.HOSTING_ABSTRACTIONS] = 11 }
                            });

        Assert.Empty(index.HostedServices);
        Assert.Equal(StubAssemblies.HOSTING_ABSTRACTIONS, Assert.Single(index.Diagnostics).Subject);
        Assert.Equal("Gate", Bound(index, Key("Gate")).ImplementationType);
    }

    [Fact]
    public void Same_named_types_from_two_assemblies_are_distinct_registrations()
    {
        var compilations = TwoAssembliesWithSameNamedTypes().Projects
                                                            .Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()!)
                                                            .ToArray();
        var index = DiIndexBuilder.Build("scope:Fixture", compilations, @"C:\fixture", CancellationToken.None);

        var alpha = Bound(index, DiIndex.TypeKey("Alpha", "Shared.Gate"));
        var beta = Bound(index, DiIndex.TypeKey("Beta", "Shared.Gate"));
        Assert.Equal(("Shared.Gate", DiLifetime.Singleton, "di:Shared.Gate@Singleton"), (alpha.ImplementationType, alpha.Lifetime, alpha.RegionId));
        Assert.Equal(("Shared.Gate", DiLifetime.Scoped, "di:Shared.Gate@Scoped"), (beta.ImplementationType, beta.Lifetime, beta.RegionId));
        Assert.Single(alpha.Sources);
        Assert.Equal(DiResolutionKind.Unregistered, index.Resolve("Shared.Gate").Kind);
        Assert.Equal(2, index.HostedServices.Count);
        Assert.All(index.HostedServices, hosted =>
        {
            Assert.Equal("Workers.SyncWorker", hosted.ImplementationType);
            Assert.Equal(HostedServiceInstanceCount.One, hosted.InstanceCount);
        });
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public void Closed_generic_over_same_named_types_from_two_assemblies_are_distinct_registrations()
    {
        static string Library(string name, string method) => $$"""
            using Microsoft.Extensions.DependencyInjection;
            namespace Ns { public sealed class Payload { } }
            public static class {{name}}Registrations
            {
                public static IServiceCollection Add{{name}}(this IServiceCollection services) => services.{{method}}(typeof(Shared.Box<Ns.Payload>));
            }
            """;
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
                ProjectReferences = [("App", "Alpha"), ("App", "Beta"), ("Alpha", "Common"), ("Beta", "Common")]
            },
            ("Common", "Box.cs", "namespace Shared { public sealed class Box<T> { public T? Value; } }"),
            ("Alpha", "Alpha.cs", Library("Alpha", "AddSingleton")),
            ("Beta", "Beta.cs", Library("Beta", "AddScoped")),
            ("App", "Program.cs", "System.Console.WriteLine();"));
        var compilations = solution.Projects.Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()!).ToArray();

        var index = DiIndexBuilder.Build("scope:Fixture", compilations, @"C:\fixture", CancellationToken.None);

        var alpha = Bound(index, DiIndex.TypeKey("Common", "Shared.Box<Alpha:Ns.Payload>"));
        var beta = Bound(index, DiIndex.TypeKey("Common", "Shared.Box<Beta:Ns.Payload>"));
        Assert.Equal((DiLifetime.Singleton, "di:Shared.Box<Ns.Payload>@Singleton"), (alpha.Lifetime, alpha.RegionId));
        Assert.Equal((DiLifetime.Scoped, "di:Shared.Box<Ns.Payload>@Scoped"), (beta.Lifetime, beta.RegionId));
        Assert.Single(alpha.Sources);
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public void Look_alike_registration_method_is_not_indexed()
    {
        var index = IndexOf("""
            namespace Custom
            {
                public static class ServiceCollectionServiceExtensions
                {
                    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddSingleton<T>(
                        this Microsoft.Extensions.DependencyInjection.IServiceCollection services, int marker) => services;
                }
            }
            public static class Uses
            {
                public static void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>
                    Custom.ServiceCollectionServiceExtensions.AddSingleton<Gate>(services, 1);
            }
            """);

        Assert.Empty(index.Registrations);
    }

    private static void AssertUnknownHostedCount(DiIndex index)
    {
        var hosted = Assert.Single(index.HostedServices);
        Assert.Equal(HostedServiceInstanceCount.Unknown, hosted.InstanceCount);
        var diagnostic = Assert.Single(index.Diagnostics);
        Assert.Equal(RootDiscoveryDiagnosticCode.UnresolvedBinding, diagnostic.Code);
        Assert.Equal("Worker", diagnostic.Subject);
    }

    private static DiBinding Bound(DiIndex index, string serviceType)
    {
        var resolution = index.Resolve(serviceType);
        Assert.Equal(DiResolutionKind.Bound, resolution.Kind);
        return resolution.Binding!;
    }

    private static string Key(string type) => DiIndex.TypeKey("Fixture", type);

    private static (string, DiLifetime) Target(DiBinding binding) => (binding.ImplementationType, binding.Lifetime);

    private static int SourceLine(string body, string text) =>
        (Prelude + Wrap(body)).Split('\n').Select((line, number) => (line, number)).Single(item => item.line.Contains(text, StringComparison.Ordinal)).number + 1;

    /// <summary>Two libraries of one executable's scope, each declaring and registering its own same-named types.</summary>
    internal static Solution TwoAssembliesWithSameNamedTypes() => FixtureSolution.CreateProjects(
        new FixtureOptions
        {
            ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
            ProjectReferences = [("App", "Alpha"), ("App", "Beta")]
        },
        ("Alpha", "Alpha.cs", SameNamedLibrary("Alpha", "AddSingleton")),
        ("Beta", "Beta.cs", SameNamedLibrary("Beta", "AddScoped")),
        ("App", "Program.cs", """
            using Microsoft.Extensions.DependencyInjection;
            System.Console.WriteLine();
            public static class Composition { public static void Compose(IServiceCollection services) { services.AddAlpha(); services.AddBeta(); } }
            """));

    private static string SameNamedLibrary(string name, string gateMethod) => $$"""
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        namespace Shared { public sealed class Gate { } }
        namespace Workers
        {
            public sealed class SyncWorker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
        }
        public static class {{name}}Registrations
        {
            public static IServiceCollection Add{{name}}(this IServiceCollection services) =>
                services.{{gateMethod}}<Shared.Gate>().AddHostedService<Workers.SyncWorker>();
        }
        """;

    private static DiIndex Index(string body, string extraMembers = "") => IndexOf(Wrap(body, extraMembers));

    private static DiIndex IndexOf(string source, FixtureOptions? options = null)
    {
        var solution = FixtureSolution.Create(options ?? new FixtureOptions(), ("Case.cs", Prelude + source));
        var compilations = solution.Projects.Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()!).ToArray();
        return DiIndexBuilder.Build("scope:Fixture", compilations, @"C:\fixture", CancellationToken.None);
    }

    private static string Wrap(string body, string extraMembers = "") => $$"""
        public static class Registrations
        {
            public static void Register(IServiceCollection services)
            {
        {{body}}
            }
            {{extraMembers}}
        }
        """;

    private const string Prelude = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.DependencyInjection.Extensions;
        using Microsoft.Extensions.Hosting;
        public interface IGate { }
        public interface IOtherGate { }
        public sealed class Gate : IGate, IOtherGate { }
        public sealed class OtherGate : IGate { }
        public sealed class Repository<T> { }
        public sealed class Worker : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
        }

        """;
}
