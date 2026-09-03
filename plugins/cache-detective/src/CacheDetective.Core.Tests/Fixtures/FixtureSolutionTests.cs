using Xunit;

namespace CacheDetective.Tests.Fixtures;

public sealed class FixtureSolutionTests
{
    [Fact]
    public async Task CreatesSolutionFromCompilingFixture()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Trivial.cs");

        var project = Assert.Single(solution.Projects);
        Assert.Single(project.Documents);
    }
}
