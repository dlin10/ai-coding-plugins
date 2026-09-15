using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class ExactSymbolsTests
{
    private const string CONTROLLER_BASE = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private static readonly SupportedAssemblyVersion MvcCore = SupportedAssemblyVersion.Framework(StubAssemblies.MVC_CORE);

    [Fact]
    public async Task Type_from_the_named_assembly_in_range_is_found()
    {
        var compilation = await Compile(new FixtureOptions(), "public sealed class HomeController : Microsoft.AspNetCore.Mvc.ControllerBase { }");

        var controllerBase = ExactSymbols.FindType(compilation, MvcCore, CONTROLLER_BASE);

        Assert.NotNull(controllerBase);
        Assert.True(ExactSymbols.IsType(compilation.GetTypeByMetadataName("HomeController")!.BaseType, MvcCore, CONTROLLER_BASE));
    }

    [Fact]
    public async Task Type_from_an_assembly_version_outside_the_range_is_not_matched()
    {
        var options = new FixtureOptions { StubVersions = new Dictionary<string, int> { [StubAssemblies.MVC_CORE] = 11 } };
        var compilation = await Compile(options, "public sealed class HomeController : Microsoft.AspNetCore.Mvc.ControllerBase { }");

        Assert.Null(ExactSymbols.FindType(compilation, MvcCore, CONTROLLER_BASE));
        Assert.False(ExactSymbols.IsType(compilation.GetTypeByMetadataName("HomeController")!.BaseType, MvcCore, CONTROLLER_BASE));
        Assert.Equal(new Version(11, 0, 0, 0), ExactSymbols.FindReferencedAssembly(compilation, StubAssemblies.MVC_CORE)!.Identity.Version);
    }

    [Fact]
    public async Task Same_named_type_declared_in_another_assembly_is_not_matched()
    {
        var compilation = await Compile(new FixtureOptions(), """
            namespace Look.Alike { public abstract class ControllerBase { } }
            public sealed class HomeController : Look.Alike.ControllerBase { }
            """);
        var lookAlike = compilation.GetTypeByMetadataName("Look.Alike.ControllerBase")!;

        Assert.False(ExactSymbols.IsType(lookAlike, MvcCore with { AssemblyName = "Fixture" }, CONTROLLER_BASE));
        Assert.False(ExactSymbols.IsType(lookAlike, MvcCore, "Look.Alike.ControllerBase"));
        Assert.True(ExactSymbols.IsType(lookAlike, new SupportedAssemblyVersion("Fixture", new Version(0, 0), new Version(1, 0)),
                                        "Look.Alike.ControllerBase"));
    }

    private static async Task<Compilation> Compile(FixtureOptions options, string source) =>
        await FixtureSolution.Create(options, ("Case.cs", source)).Projects.Single().GetCompilationAsync()
        ?? throw new InvalidOperationException();
}
