using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed record EngineRun(ScopeAnalysisInput Input, AccessExtractionResult Extraction, PairAnalysis Pairs)
{
    public IReadOnlyList<Access> Accesses => Extraction.Accesses;

    public Access Single(string member, AccessOperation operation) =>
        Assert.Single(Accesses, access => access.Resource.Member.Name == member && access.Operation == operation);

    public IReadOnlyList<Access> Of(string member) => Accesses.Where(access => access.Resource.Member.Name == member).ToArray();

    public int Skipped(string reason) => Extraction.Skips.GetValueOrDefault(reason);
}

/// <summary>Runs the real pipeline on one source file: the DI index, both built-in providers, injection bindings, IR
/// lowering of every body a root reaches, then access extraction and pairing.</summary>
public static class EngineFixture
{
    public const string ROOT_DIRECTORY = @"C:\fixture";

    public const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;

        """;

    /// <summary>Registers and maps controllers so controller roots exist; append the case's registrations inside
    /// <c>Register</c> through <paramref name="registrations"/>.</summary>
    public static string Startup(string registrations = "") => $$"""

        public static class Startup
        {
            public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
            {
                services.AddControllers();
                app.MapControllers();
                {{registrations}}
            }
        }
        """;

    public static EngineRun Analyze(string source, string scopeId = "scope:Fixture") =>
        AnalyzeScope(FixtureSolution.Create(("Case.cs", Usings + source)), scopeId);

    public static EngineRun AnalyzeScope(Solution solution, string scopeId)
    {
        var compilations = solution.Projects.Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()!).ToArray();
        var index = DiIndexBuilder.Build(scopeId, compilations, ROOT_DIRECTORY, CancellationToken.None);
        var context = new RootDiscoveryContext(scopeId, compilations, ROOT_DIRECTORY, index, CancellationToken.None);
        var roots = ProviderRegistry.BuiltIn.Providers.SelectMany(provider => provider.Discover(context).Roots).ToArray();
        var bindings = InjectionBindings.Discover(compilations, index, ROOT_DIRECTORY, CancellationToken.None);

        var bodies = new Dictionary<string, IrBody>(StringComparer.Ordinal);
        var wanted = roots.Select(root => IrLowering.EnclosingMethodBodyId(root.Entry.BodyKey)).ToHashSet(StringComparer.Ordinal);
        foreach (var compilation in compilations)
        {
            foreach (var method in Methods(compilation.Assembly.GlobalNamespace))
            {
                if (!wanted.Contains(IrLowering.RootBodyId(method)))
                    continue;
                var lowered = IrLowering.Lower(method, compilation, ROOT_DIRECTORY, CancellationToken.None);
                foreach (var body in lowered.NestedBodies.Prepend(lowered.Body))
                    bodies[body.BodyId] = body;
            }
        }

        var input = new ScopeAnalysisInput(scopeId, roots, bodies, index, bindings);
        var extraction = AccessExtraction.Extract(input);
        return new EngineRun(input, extraction, AccessPairing.Pair(extraction.Accesses));
    }

    private static IEnumerable<IMethodSymbol> Methods(INamespaceSymbol @namespace) =>
        @namespace.GetTypeMembers().SelectMany(NestedAndSelf)
                  .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
                  .Where(method => method.DeclaringSyntaxReferences.Length != 0 && method.MethodKind is MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation)
                  .Concat(@namespace.GetNamespaceMembers().SelectMany(Methods));

    private static IEnumerable<INamedTypeSymbol> NestedAndSelf(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(NestedAndSelf));
}
