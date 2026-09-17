using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
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
        Assert.Equal("di:Gate@Singleton", binding.RegionDisplay);
        Assert.Equal(DiIndex.RegionId(Key("Gate"), Key("Gate"), DiLifetime.Singleton, 1), binding.RegionId);
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
        Assert.Equal("di:Gate@Scoped", binding.RegionDisplay);
        Assert.Equal(DiIndex.RegionId(Key("IGate"), Key("Gate"), DiLifetime.Scoped, 1), binding.RegionId);
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
    public void Two_services_on_one_implementation_are_two_regions()
    {
        var index = Index("""
            services.AddSingleton<IGate, Gate>();
            services.AddSingleton<IOtherGate, Gate>();
            """);

        var first = Bound(index, Key("IGate"));
        var second = Bound(index, Key("IOtherGate"));
        Assert.NotEqual(first.RegionId, second.RegionId);
        Assert.Equal(("di:Gate@Singleton", "di:Gate@Singleton"), (first.RegionDisplay, second.RegionDisplay));
        Assert.NotEqual(first.ServiceType, second.ServiceType);
    }

    [Fact]
    public void Resolution_lists_every_registration_in_the_total_order()
    {
        var index = IndexOf(("B.cs", Wrap("services.AddSingleton<IGate, Gate>();", className: "Second")),
                            ("A.cs", Wrap("services.AddScoped<IGate, OtherGate>();", className: "First")));

        var resolution = index.Resolve(Key("IGate"));
        Assert.Equal(DiResolutionKind.Ambiguous, resolution.Kind);
        Assert.Equal(["A.cs", "B.cs"], resolution.Registrations.Select(registration => registration.Source.Path));
    }

    [Fact]
    public void Factory_registration_records_its_registration_call()
    {
        var solution = FixtureSolution.Create(
            new FixtureOptions { OutputKind = OutputKind.ConsoleApplication },
            ("Program.cs", Usings + """
                IServiceCollection services = null!;
                services.AddSingleton<Gate>(_ => new Gate());

                """ + Types));
        var compilation = Compilations(solution).Single();
        var index = Build([compilation]);

        var registration = Assert.Single(index.Registrations);
        Assert.Equal((DiRegistrationForm.Factory, MAIN), (registration.Form, registration.BodyId));
        Assert.StartsWith("body:Microsoft.Extensions.DependencyInjection.Abstractions:M:Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton``1",
                          RegistrationCall(compilation, registration).TargetMethodId, StringComparison.Ordinal);

        var run = EngineFixture.ReachScope(solution, "scope:Fixture");
        Assert.Equal([MAIN + "#lambda1"], run.Input.Program.Method(MAIN)!.NestedBodyIds);
        // The entry point holds a factory registration, so the reachable set lowers it as a startup construction.
        Assert.Equal([MAIN, MAIN + "#lambda1"], run.Result.Bodies.Keys.Where(body => body.StartsWith(MAIN, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Contains(MAIN, run.LoweredMembers);
        Assert.DoesNotContain(run.Input.Roots, root => root.Entry.BodyKey.StartsWith(MAIN, StringComparison.Ordinal));
    }

    [Fact]
    public void Method_group_factory_records_its_registration_call()
    {
        var (compilation, index) = IndexWithCompilation(Wrap("", """
            public static IServiceCollection AddGate(this IServiceCollection services) => services.AddSingleton<Gate>(Make);
            private static Gate Make(IServiceProvider provider) => new Gate();
            """));

        var registration = Assert.Single(index.Registrations);
        Assert.Equal((DiRegistrationForm.Factory, "body:Fixture:M:Registrations.AddGate(Microsoft.Extensions.DependencyInjection.IServiceCollection)"),
                     (registration.Form, registration.BodyId));
        Assert.Contains("AddSingleton", RegistrationCall(compilation, registration).Method, StringComparison.Ordinal);
    }

    [Fact]
    public void Instance_registration_records_its_registration_call()
    {
        const string body = """
            var gate = new Gate();
            services.AddSingleton(gate);
            """;
        var (compilation, index) = IndexWithCompilation(Wrap(body));

        var registration = Assert.Single(index.Registrations);
        Assert.Equal((DiRegistrationForm.Instance, REGISTER), (registration.Form, registration.BodyId));
        var call = RegistrationCall(compilation, registration);
        Assert.Contains("AddSingleton", call.Method, StringComparison.Ordinal);
        Assert.Equal(SourceLine(body, "services.AddSingleton(gate);"), call.Provenance.Span.StartLine);
    }

    [Fact]
    public void Identical_registrations_are_numbered_and_injection_binds_the_last()
    {
        var compilations = Compilations(Solution(("Case.cs", Wrap("""
            services.AddSingleton<IGate, Gate>();
            services.AddSingleton<IGate, Gate>();
            """, "public sealed class Holder { public Holder(IGate gate) { GC.KeepAlive(gate); } }"))));
        var index = Build(compilations);

        Assert.Equal([1, 2], index.Registrations.Select(registration => registration.Number));
        var binding = Bound(index, Key("IGate"));
        Assert.Equal(DiIndex.RegionId(Key("IGate"), Key("Gate"), DiLifetime.Singleton, 2), binding.RegionId);
        Assert.Equal("di:Gate@Singleton#2", binding.RegionDisplay);
        Assert.Equal(2, binding.Sources.Count);

        var bindings = InjectionBindings.Discover(compilations, index, @"C:\fixture", CancellationToken.None);
        var holder = Assert.Single(bindings, type => type.TypeKey == Key("Registrations.Holder"));
        Assert.Equal(binding.RegionId, Assert.Single(holder.ConstructorParameters).Resolution.Binding!.RegionId);
    }

    [Fact]
    public void Identical_registrations_across_two_documents_are_numbered_by_path_then_position()
    {
        var index = IndexOf(("B.cs", Wrap("services.AddSingleton<Gate>();", className: "Second")),
                            ("A.cs", Wrap("""
                                services.AddSingleton<Gate>(); services.AddSingleton<Gate>();
                                services.AddSingleton<Gate>();
                                """, className: "First")));

        Assert.Equal([("A.cs", 1), ("A.cs", 2), ("A.cs", 3), ("B.cs", 4)],
                     index.Registrations.Select(registration => (registration.Source.Path, registration.Number)));
        var (first, second, third) = (index.Registrations[0].Source, index.Registrations[1].Source, index.Registrations[2].Source);
        Assert.True(first.StartLine == second.StartLine && first.StartColumn < second.StartColumn && second.StartLine < third.StartLine);
        var binding = Bound(index, Key("Gate"));
        Assert.Equal(("di:Gate@Singleton#4", 4), (binding.RegionDisplay, binding.Sources.Count));
        Assert.Equal("B.cs", binding.Sources[^1].Path);
    }

    [Fact]
    public void Identical_registrations_in_a_linked_file_of_two_projects_are_ordered_by_assembly()
    {
        var (solution, projects) = LinkedFile(new Dictionary<string, string> { ["Alpha"] = "Zed", ["Beta"] = "Able" });

        var index = DiIndexBuilder.Build("scope:Fixture", projects, @"C:\fixture", CancellationToken.None);

        Assert.Equal([("Able", "Beta/Beta.csproj", 1), ("Zed", "Alpha/Alpha.csproj", 1)],
                     index.Registrations.Select(registration => (registration.Assembly, registration.ProjectPath, registration.Number)));
        Assert.All(index.Registrations, registration => Assert.Equal(LINKED_PATH, registration.Source.Path));
        Assert.Equal(2, solution.Projects.Count());
    }

    [Fact]
    public void Identical_registrations_in_a_linked_file_of_two_same_named_projects_are_ordered_by_project_path()
    {
        var (_, projects) = LinkedFile(new Dictionary<string, string> { ["Alpha"] = "Shared", ["Beta"] = "Shared" });

        var index = DiIndexBuilder.Build("scope:Fixture", projects.Reverse(), @"C:\fixture", CancellationToken.None);

        Assert.Equal([("Shared", "Alpha/Alpha.csproj", 1), ("Shared", "Beta/Beta.csproj", 2)],
                     index.Registrations.Select(registration => (registration.Assembly, registration.ProjectPath, registration.Number)));
        Assert.Equal(index.Registrations[0].BodyId, index.Registrations[1].BodyId);
        Assert.Equal("di:Gate@Singleton#2", Bound(index, DiIndex.TypeKey("Shared", "Gate")).RegionDisplay);
    }

    [Fact]
    public void Registration_in_a_document_outside_the_repository_root_orders_by_its_full_path()
    {
        var outside = Path.Combine(Path.GetTempPath(), "concurrency-hunter-linked", "Outside.cs");
        var solution = Solution(("A.cs", Wrap("services.AddSingleton<Gate>();", className: "First")),
                                ("B.cs", Wrap("services.AddSingleton<Gate>();", className: "Second")));
        solution = solution.AddDocument(DocumentId.CreateNewId(solution.ProjectIds.Single()), "Outside.cs",
                                        Usings + Wrap("services.AddSingleton<Gate>();", className: "Third"), filePath: outside);

        var index = Build(Compilations(solution));

        var fullPath = Path.GetFullPath(outside).Replace('\\', '/');
        Assert.True(string.CompareOrdinal(fullPath, "B.cs") > 0);
        Assert.Equal(["A.cs", "B.cs", fullPath], index.Registrations.Select(registration => registration.Source.Path));
        Assert.DoesNotContain("..", index.Registrations[^1].Source.Path, StringComparison.Ordinal);
        Assert.Equal(3, index.Registrations[^1].Number);
    }

    [Fact]
    public void Registration_display_gains_an_ordinal_only_from_the_second_identical_one()
    {
        var index = Index("""
            services.AddSingleton<Gate>();
            services.AddSingleton<IGate, Gate>();
            services.AddSingleton<Gate>();
            services.AddSingleton<Gate>();
            """);

        Assert.Equal(["di:Gate@Singleton", "di:Gate@Singleton", "di:Gate@Singleton#2", "di:Gate@Singleton#3"],
                     index.Registrations.Select(registration => DiIndex.RegionDisplay(registration.ImplementationType!, registration.Lifetime,
                                                                                    registration.Number)));
        Assert.Equal("di:Gate@Singleton#3", Bound(index, Key("Gate")).RegionDisplay);
        Assert.Equal("di:Gate@Singleton", Bound(index, Key("IGate")).RegionDisplay);
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
        Assert.Equal(("Shared.Gate", DiLifetime.Singleton, "di:Shared.Gate@Singleton"), (alpha.ImplementationType, alpha.Lifetime, alpha.RegionDisplay));
        Assert.Equal(("Shared.Gate", DiLifetime.Scoped, "di:Shared.Gate@Scoped"), (beta.ImplementationType, beta.Lifetime, beta.RegionDisplay));
        Assert.NotEqual(alpha.RegionId, beta.RegionId);
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
        Assert.Equal((DiLifetime.Singleton, "di:Shared.Box<Ns.Payload>@Singleton"), (alpha.Lifetime, alpha.RegionDisplay));
        Assert.Equal((DiLifetime.Scoped, "di:Shared.Box<Ns.Payload>@Scoped"), (beta.Lifetime, beta.RegionDisplay));
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

    private const string MAIN = "body:Fixture:M:Program.{Main}$(System.String[])";
    private const string REGISTER = "body:Fixture:M:Registrations.Register(Microsoft.Extensions.DependencyInjection.IServiceCollection)";
    private const string LINKED_PATH = "Shared/Registrations.cs";

    /// <summary>One document compiled into the projects Alpha and Beta, with their assembly names; the projects in name order.</summary>
    private static (Solution Solution, IReadOnlyList<(Compilation Compilation, string? ProjectFilePath)> Projects) LinkedFile(
        IReadOnlyDictionary<string, string> assemblyNames)
    {
        var solution = FixtureSolution.CreateProjects(new FixtureOptions { ProjectAssemblyNames = assemblyNames },
                                                      ("Alpha", "Alpha.cs", "public static class AlphaMarker { }"),
                                                      ("Beta", "Beta.cs", "public static class BetaMarker { }"));
        foreach (var projectId in solution.ProjectIds.ToArray())
        {
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Registrations.cs",
                                            Usings + "public sealed class Gate { }\n" + Wrap("services.AddSingleton<Gate>();"),
                                            filePath: @"C:\fixture\" + LINKED_PATH.Replace('/', '\\'));
        }

        var projects = solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal)
                               .Select(project => (project.GetCompilationAsync().GetAwaiter().GetResult()!, project.FilePath))
                               .ToArray();
        return (solution, projects);
    }

    /// <summary>The call operation a registration records, found in the lowered body it names.</summary>
    private static IrCallOperation RegistrationCall(Compilation compilation, DiRegistration registration)
    {
        static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceOrTypeSymbol container) =>
            container.GetTypeMembers().SelectMany(type => AllTypes(type).Prepend(type))
                     .Concat(container is INamespaceSymbol @namespace ? @namespace.GetNamespaceMembers().SelectMany(AllTypes) : []);

        var memberId = IrLowering.EnclosingMethodBodyId(registration.BodyId!);
        var method = AllTypes(compilation.Assembly.GlobalNamespace).SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
                                                                .Single(candidate => IrLowering.RootBodyId(candidate) == memberId);
        var lowered = IrLowering.Lower(method, compilation, @"C:\fixture", CancellationToken.None);
        var body = Assert.Single(lowered.NestedBodies.Prepend(lowered.Body), candidate => candidate.BodyId == registration.BodyId);
        return Assert.IsType<IrCallOperation>(Assert.Single(body.Blocks.SelectMany(block => block.Operations),
                                                            operation => operation.Id == registration.OperationId));
    }

    private static DiIndex Index(string body, string extraMembers = "") => IndexOf(Wrap(body, extraMembers));

    private static DiIndex IndexOf(string source, FixtureOptions? options = null) =>
        Build(Compilations(FixtureSolution.Create(options ?? new FixtureOptions(), ("Case.cs", Prelude + source))));

    /// <summary>Indexes several documents of one project; the first declares the prelude's types.</summary>
    private static DiIndex IndexOf(params (string Path, string Source)[] files) => Build(Compilations(Solution(files)));

    private static (Compilation Compilation, DiIndex Index) IndexWithCompilation(string source)
    {
        var compilations = Compilations(FixtureSolution.Create(("Case.cs", Prelude + source)));
        return (compilations.Single(), Build(compilations));
    }

    private static Solution Solution(params (string Path, string Source)[] files) =>
        FixtureSolution.Create(files.Select((file, index) => (file.Path, (index == 0 ? Prelude : Usings) + file.Source)).ToArray());

    private static Compilation[] Compilations(Solution solution) =>
        solution.Projects.Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()!).ToArray();

    private static DiIndex Build(IEnumerable<Compilation> compilations) =>
        DiIndexBuilder.Build("scope:Fixture", compilations, @"C:\fixture", CancellationToken.None);

    private static string Wrap(string body, string extraMembers = "", string className = "Registrations") => $$"""
        public static class {{className}}
        {
            public static void Register(IServiceCollection services)
            {
        {{body}}
            }
            {{extraMembers}}
        }
        """;

    private const string Prelude = Usings + Types;

    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.DependencyInjection.Extensions;
        using Microsoft.Extensions.Hosting;

        """;

    private const string Types = """
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
