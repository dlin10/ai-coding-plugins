using System.Text.Json;
using PlanForge.Vendors;
using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

public sealed class CodexCatalogTests
{
    [Fact]
    public void Parses_the_live_model_catalogue()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "models": [
                { "slug": "hidden-model", "visibility": "hidden", "priority": 0 },
                { "slug": "gpt-5.6-terra", "visibility": "list", "priority": 2,
                  "display_name": "GPT-5.6 Terra", "description": "desc-terra",
                  "default_reasoning_level": "low",
                  "supported_reasoning_levels": [ { "effort": "low" }, { "effort": "max" } ] },
                { "slug": "gpt-5.6-sol", "visibility": "list", "priority": 1,
                  "display_name": "GPT-5.6 Sol", "description": "desc-sol",
                  "default_reasoning_level": "low",
                  "supported_reasoning_levels": [ { "effort": "low" } ] },
                { "slug": "gpt-5.6-luna", "visibility": "list", "priority": 3,
                  "display_name": "GPT-5.6 Luna", "description": "desc-luna",
                  "default_reasoning_level": "max",
                  "supported_reasoning_levels": [ { "effort": "max" } ] }
              ]
            }
            """);

        var models = CodexCliVendor.ParseModels(document.RootElement);

        Assert.Equal(["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"], models.Select(model => model.Id));
        Assert.True(models[0].IsDefault);
        Assert.False(models[1].IsDefault);
        Assert.False(models[2].IsDefault);
        Assert.Equal(["low"], models[0].Efforts);
        Assert.Equal(["low", "max"], models[1].Efforts);
        Assert.Equal(["max"], models[2].Efforts);
        Assert.Equal("GPT-5.6 Sol", models[0].DisplayName);
        Assert.Equal("desc-sol", models[0].Description);
        Assert.Equal("low", models[0].DefaultEffort);
    }

    /// <summary>
    /// `codex debug models` names the speed tiers a model offers. `service_tier="fast"` asks for the
    /// one `additional_speed_tiers` calls `fast`, and the tier's description is the only price hint
    /// the user gets — it differs by model. Measured against codex-cli 0.157.0 on 2026-09-25.
    /// </summary>
    [Fact]
    public void A_model_offers_fast_on_every_effort_only_where_codex_lists_the_tier()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "models": [
                { "slug": "gpt-6-astra", "visibility": "list", "priority": 1,
                  "supported_reasoning_levels": [ { "effort": "low" }, { "effort": "high" } ],
                  "service_tiers": [ { "id": "priority", "name": "Fast", "description": "2x speed, increased usage" } ],
                  "additional_speed_tiers": [ "fast" ] },
                { "slug": "gpt-5.2", "visibility": "list", "priority": 2,
                  "supported_reasoning_levels": [ { "effort": "low" } ] }
              ]
            }
            """);

        var models = CodexCliVendor.ParseModels(document.RootElement);

        Assert.Equal(["low", "high"], models[0].FastEfforts);
        Assert.Equal("2x speed, increased usage", models[0].FastHint);
        Assert.Empty(models[1].FastEfforts);
        Assert.Null(models[1].FastHint);
    }

    [Fact]
    public void A_document_with_no_models_array_throws()
    {
        using var document = JsonDocument.Parse("""{ "notModels": [] }""");

        Assert.Throws<VendorException>(() => CodexCliVendor.ParseModels(document.RootElement));
    }

    [Fact]
    public void An_entry_missing_a_slug_is_skipped_rather_than_thrown_on()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "models": [
                { "visibility": "list", "priority": 1 },
                { "slug": "gpt-5.6-sol", "visibility": "list", "priority": 2 }
              ]
            }
            """);

        var models = CodexCliVendor.ParseModels(document.RootElement);

        Assert.Equal(["gpt-5.6-sol"], models.Select(model => model.Id));
    }
}
