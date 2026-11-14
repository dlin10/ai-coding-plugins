using System.Reflection;
using Xunit;

namespace CodexSessionPulse.Infrastructure.Tests;

/// <summary>Smoke test of the scaffold: the Infrastructure assembly is built and loadable.</summary>
public sealed class AssemblyTests
{
    /// <summary>The Infrastructure assembly loads by name from the test output.</summary>
    [Fact]
    public void InfrastructureAssembly_Loads()
    {
        Assembly assembly = Assembly.Load("CodexSessionPulse.Infrastructure");

        Assert.Equal("CodexSessionPulse.Infrastructure", assembly.GetName().Name);
    }
}
