using PlanForge.Mcp;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

public sealed class GateSettingsTests
{
    [Fact]
    public void Empty_settings_are_stored_as_nothing()
    {
        var settings = GateSettings.Validate(new Dictionary<string, string>(), []);

        Assert.Null(settings.Environment);
        Assert.Null(settings.BuilderRoots);
    }

    [Fact]
    public void A_relative_builder_root_is_refused_by_name()
    {
        var error = Assert.Throws<ArgumentRejectedException>(() => GateSettings.Validate(null, ["../eShop"]));

        Assert.Contains("../eShop", error.Message, StringComparison.Ordinal);
        Assert.Contains("absolute", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variable_name_with_an_equals_sign_or_a_space_is_refused()
    {
        Assert.Throws<ArgumentRejectedException>(() => GateSettings.Validate(new Dictionary<string, string> { ["A=B"] = "x" }, null));
        Assert.Throws<ArgumentRejectedException>(() => GateSettings.Validate(new Dictionary<string, string> { ["A B"] = "x" }, null));
    }

    [Fact]
    public void Valid_settings_pass_through_unchanged()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "eShop"));
        var settings = GateSettings.Validate(new Dictionary<string, string> { ["CD_TEST_SQL_CONN"] = "Server=." }, [root]);

        Assert.Equal("Server=.", settings.Environment!["CD_TEST_SQL_CONN"]);
        Assert.Equal([root], settings.BuilderRoots);
    }
}
