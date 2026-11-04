using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The libraries that answered <c>library-does-not-compile</c> compile now: the three ASP.NET Core assemblies of the demo and the
/// fifteen libraries of eShop <c>7e5ae7b5</c> that B3 refused, each resolved from the installed frameworks and the NuGet global packages
/// folder as <c>generate</c> resolves them and compiled by <see cref="LibraryCompilation.Compile(ImplementationAssembly, CancellationToken)"/>
/// (SPEC TD-034b, ADR 0028). A row leaves nothing in the compilation cache, so the process holds no more than one library at a time.</summary>
public sealed class LibraryCompileAcceptanceTests
{
    private const string DEMO_VERSION = "10.0.0.0";
    private const string ESHOP_PLATFORM = "net8.0";

    [Theory]
    [InlineData("Microsoft.AspNetCore.Mvc.Core")]
    [InlineData("Microsoft.AspNetCore.Mvc")]
    [InlineData("Microsoft.AspNetCore.Routing")]
    public void Demo_AspNetCore_library_compiles(string assemblyName) =>
        AssertCompiles(assemblyName, DEMO_VERSION, null, null);

    // The assembly version of a framework row and the package version of a package row; a package row is resolved for net8.0.
    [RequiresEShopTheory]
    [InlineData("HealthChecks.SqlServer", "6.0.2", "AspNetCore.HealthChecks.SqlServer")]
    [InlineData("Azure.Identity", "1.5.0-beta.3", "Azure.Identity")]
    [InlineData("Azure.Identity", "1.9.0-beta.1", "Azure.Identity")]
    [InlineData("Duende.IdentityServer", "6.2.0", "Duende.IdentityServer")]
    [InlineData("Grpc.AspNetCore.Server", "2.50.0", "Grpc.AspNetCore.Server")]
    [InlineData("Microsoft.ApplicationInsights.AspNetCore", "2.22.0-beta1", "Microsoft.ApplicationInsights.AspNetCore")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "8.0.0", "Microsoft.EntityFrameworkCore.SqlServer")]
    [InlineData("Serilog.AspNetCore", "6.1.0-dev-00289", "Serilog.AspNetCore")]
    [InlineData("StackExchange.Redis", "2.5.61", "StackExchange.Redis")]
    [InlineData("Microsoft.AspNetCore.Mvc.Core", "8.0.0.0", null)]
    [InlineData("Microsoft.AspNetCore.Mvc", "8.0.0.0", null)]
    [InlineData("Microsoft.AspNetCore.Mvc.ViewFeatures", "8.0.0.0", null)]
    [InlineData("Microsoft.AspNetCore.Diagnostics", "8.0.0.0", null)]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel.Core", "8.0.0.0", null)]
    [InlineData("System.Text.Json", "8.0.0.0", null)]
    public void EShop_library_compiles(string assemblyName, string version, string? packageId) =>
        AssertCompiles(assemblyName, version, packageId, packageId is null ? null : ESHOP_PLATFORM);

    /// <summary>Resolves a library as <c>generate</c> does and checks that its compilation has no <see cref="LibraryCompilationResult.Reason"/>,
    /// naming the resolution or the compiler errors when it has one; the compilation leaves the cache before the row ends. A package or a
    /// framework the machine lacks fails the row, never skips it.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="version">The assembly version for a framework member, the package version with a package id.</param>
    /// <param name="packageId">The package that ships the assembly; <c>null</c> for a framework member.</param>
    /// <param name="framework">The platform <c>net&lt;major&gt;.0</c> of a package member; <c>null</c> for a framework member.</param>
    private static void AssertCompiles(string assemblyName, string version, string? packageId, string? framework)
    {
        var resolution = ImplementationAssemblies.ForThisProcess().Resolve(assemblyName, version, packageId, framework);
        Assert.True(resolution.Reason is null, $"{assemblyName} {version}: {resolution.Reason}: {resolution.Detail}");

        var ownership = new CompilationOwnership();
        try
        {
            LibraryCompilationResult library;
            using (ownership.Open())
                library = LibraryCompilation.Compile(resolution.Assembly!, CancellationToken.None);

            Assert.True(library.Reason is null, $"{assemblyName} {version}: {library.Reason}: {string.Join(", ", library.Errors)}");
        }
        finally
        {
            LibraryCompilation.Release(ownership.Created);
        }
    }
}
