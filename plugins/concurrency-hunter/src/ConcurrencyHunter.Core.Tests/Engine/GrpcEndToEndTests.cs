using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A gRPC service method is a root like an HTTP action, from discovery to the finding.</summary>
public sealed class GrpcEndToEndTests
{
    private const string Source = """
        using System;
        using System.Threading.Tasks;
        using Grpc.Core;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.Extensions.DependencyInjection;

        public sealed class Request { }
        public sealed class Reply { }

        public static partial class Greeter
        {
            [BindServiceMethod(typeof(Greeter), "BindService")]
            public abstract partial class GreeterBase
            {
                public virtual Task<Reply> SayHello(Request request, ServerCallContext context) => throw new NotSupportedException();
            }
        }

        public sealed class GreetingLog { public string? Last; }

        public sealed class GreeterService : Greeter.GreeterBase
        {
            private readonly GreetingLog _log;

            public GreeterService(GreetingLog log) => _log = log;

            public override Task<Reply> SayHello(Request request, ServerCallContext context)
            {
                _log.Last = "hello";
                return Task.FromResult(new Reply());
            }
        }

        public static class Startup
        {
            public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
            {
                services.AddGrpc();
                services.AddSingleton<GreetingLog>();
                app.MapGrpcService<GreeterService>();
            }
        }
        """;

    [Fact]
    public async Task Grpc_method_writing_a_singleton_pairs_with_itself()
    {
        var result = await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Greeter.cs", Source)), EngineFixture.ROOT_DIRECTORY,
                                                         ProviderRegistry.BuiltIn, CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal("di:GreetingLog@Singleton", finding.Resource.Region);
        Assert.Equal(["Last"], finding.Resource.AccessPath);
        Assert.Equal("GreeterService.SayHello(Request, ServerCallContext)", finding.AccessA.Symbol);
        Assert.Equal(finding.AccessA.Symbol, finding.AccessB.Symbol);
        Assert.Equal((AspNetCoreRootProvider.PROVIDER_ID, AspNetCoreRootProvider.GRPC_METHOD), (finding.AccessA.Root.ProviderId, finding.AccessA.Root.RootKind));
        Assert.Equal("grpc-method", finding.AccessA.Root.RootKind);
    }

    [Fact]
    public async Task Grpc_service_registered_as_a_singleton_is_the_one_receiver_of_every_call()
    {
        var source = Source.Replace("services.AddSingleton<GreetingLog>();", "services.AddSingleton<GreetingLog>(); services.AddSingleton<GreeterService>();",
                                    StringComparison.Ordinal)
                           .Replace("public GreeterService(GreetingLog log) => _log = log;",
                                    "public GreeterService(GreetingLog log) => _log = log;\n    public int Calls;", StringComparison.Ordinal)
                           .Replace("_log.Last = \"hello\";", "Calls++;", StringComparison.Ordinal);
        var result = await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Greeter.cs", source)), EngineFixture.ROOT_DIRECTORY,
                                                         ProviderRegistry.BuiltIn, CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("DCA1002", finding.RuleId);
        Assert.Equal("di:GreeterService@Singleton", finding.Resource.Region);
        Assert.Equal(["Calls"], finding.Resource.AccessPath);
    }
}
