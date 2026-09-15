using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class StubAssemblyTests
{
    /// <summary>Every registration, root and endpoint shape the demo uses, so a stub signature that drifts from
    /// the real one fails here rather than inside a provider case.</summary>
    private const string DemoPatterns = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.DependencyInjection.Extensions;
        using Microsoft.Extensions.Hosting;

        public sealed class State { public string? Value { get; set; } }
        public interface ISink { void Record(string value); }
        public sealed class Sink : ISink { public void Record(string value) { } }

        [ApiController]
        [Route("cases/state")]
        public sealed class StateController(State state) : ControllerBase
        {
            [HttpPost] public void Post([FromServices] ISink sink, string value) { state.Value = value; sink.Record(value); }
            [HttpGet("{id}")] public string? Get(int id) => state.Value;
            [NonAction] public void Helper() { }
        }

        [Route("cases/poco")]
        public sealed class PocoController { [HttpPut] public void Put(string value) { } }

        public sealed class Worker : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            public override Task StopAsync(CancellationToken cancellationToken) => base.StopAsync(cancellationToken);
        }

        public sealed class Warmup : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public static class Endpoints
        {
            public static void Set(State state, string value) => state.Value = value;
            public static string? Get(State state) => state.Value;
        }

        public static class Composition
        {
            public static IServiceCollection Register(IServiceCollection services)
            {
                services.AddControllers();
                services.TryAddSingleton<State>();
                services.AddKeyedScoped<State>("keyed");
                return services.AddSingleton<State>()
                               .AddScoped<State>()
                               .AddTransient<State>(_ => new State())
                               .AddSingleton<ISink, Sink>()
                               .AddSingleton(new State())
                               .AddHostedService<Worker>()
                               .AddHostedService(_ => new Warmup());
            }

            public static void Map(IEndpointRouteBuilder app, IServiceProvider services)
            {
                app.MapControllers();
                app.MapPost("/state", Endpoints.Set);
                app.MapGet("/state", Endpoints.Get);
                app.MapGet("/lambda", () => "value");
                app.MapGet("/request", (HttpContext context) => Task.CompletedTask);
                app.MapMethods("/methods", ["PUT"], (State state) => state.Value);
                app.MapGroup("/group").MapDelete("/{id}", (int id) => id);
                app.MapFallback(() => "fallback");
                FallbackEndpointRouteBuilderExtensions.MapFallback(app, context => Task.CompletedTask);
                services.GetRequiredService<State>().Value = "startup";
            }
        }
        """;

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task Demo_patterns_compile_against_every_supported_stub_version(int major)
    {
        var options = new FixtureOptions { StubVersions = StubAssemblies.Names.ToDictionary(name => name, _ => major) };

        var compilation = await Compile(FixtureSolution.Create(options, ("Demo.cs", DemoPatterns)));

        Assert.All(StubAssemblies.Names, name =>
            Assert.Equal(new Version(major, 0, 0, 0), Referenced(compilation, name).Identity.Version));
    }

    [Fact]
    public async Task Request_delegate_lambda_binds_the_request_delegate_overload_and_other_lambdas_bind_delegate()
    {
        var compilation = await Compile(FixtureSolution.Create(("Demo.cs", DemoPatterns)));
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var calls = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                        .Select(call => model.GetSymbolInfo(call).Symbol as IMethodSymbol)
                        .Where(method => method?.Name == "MapGet")
                        .Select(method => method!.Parameters[^1].Type.Name)
                        .ToArray();

        Assert.Equal(["Delegate", "Delegate", "RequestDelegate"], calls);
    }

    [Fact]
    public async Task Stubs_at_different_versions_resolve_together()
    {
        var options = new FixtureOptions
        {
            StubVersions = new Dictionary<string, int>
            {
                [StubAssemblies.DEPENDENCY_INJECTION_ABSTRACTIONS] = 9,
                [StubAssemblies.HOSTING_ABSTRACTIONS] = 8,
                [StubAssemblies.MVC_CORE] = 7
            }
        };

        var compilation = await Compile(FixtureSolution.Create(options, ("Demo.cs", DemoPatterns)));

        Assert.Equal(new Version(9, 0, 0, 0), Referenced(compilation, StubAssemblies.DEPENDENCY_INJECTION_ABSTRACTIONS).Identity.Version);
        Assert.Equal(new Version(8, 0, 0, 0), Referenced(compilation, StubAssemblies.HOSTING_ABSTRACTIONS).Identity.Version);
        Assert.Equal(new Version(7, 0, 0, 0), Referenced(compilation, StubAssemblies.MVC_CORE).Identity.Version);
        Assert.Equal(new Version(10, 0, 0, 0), Referenced(compilation, StubAssemblies.ROUTING).Identity.Version);
    }

    [Fact]
    public async Task Projects_get_references_output_kinds_and_extra_assemblies()
    {
        var options = new FixtureOptions
        {
            ProjectReferences = [("App", "Library")],
            ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
            ExtraAssemblyNames = ["Grpc.AspNetCore.Server"],
            StubVersions = new Dictionary<string, int> { ["Grpc.AspNetCore.Server"] = 2 }
        };

        var solution = FixtureSolution.CreateProjects(options,
            ("Library", "Shared.cs", "public static class Shared { public static string? Value; }"),
            ("App", "Program.cs", "Shared.Value = \"app\";"));

        var app = solution.Projects.Single(project => project.Name == "App");
        var library = solution.Projects.Single(project => project.Name == "Library");
        Assert.Equal(OutputKind.ConsoleApplication, app.CompilationOptions!.OutputKind);
        Assert.Equal(OutputKind.DynamicallyLinkedLibrary, library.CompilationOptions!.OutputKind);
        Assert.Equal([library.Id], app.ProjectReferences.Select(reference => reference.ProjectId));
        Assert.Equal(new Version(2, 0, 0, 0), Referenced(await Compile(solution, "App"), "Grpc.AspNetCore.Server").Identity.Version);
    }

    [Fact]
    public void Unknown_stub_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => StubAssemblies.Get("Microsoft.AspNetCore.Mvc.ViewFeatures", 10));
    }

    private static async Task<Compilation> Compile(Solution solution, string project = "Fixture") =>
        await solution.Projects.Single(candidate => candidate.Name == project).GetCompilationAsync()
        ?? throw new InvalidOperationException();

    private static IAssemblySymbol Referenced(Compilation compilation, string name) =>
        compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>()
                   .Single(assembly => assembly.Identity.Name == name);
}
