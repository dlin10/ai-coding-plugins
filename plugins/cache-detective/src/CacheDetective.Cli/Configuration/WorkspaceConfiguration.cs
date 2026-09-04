using System.Text.Json;
using System.Text.Json.Serialization;
using CacheDetective.Rules;

namespace CacheDetective.Configuration;

public sealed class WorkspaceConfiguration
{
    public const int CurrentVersion = 1;
    public const double DefaultBudgetSeconds = StalenessBudget.DefaultSeconds;

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("root")]
    public string Root { get; init; } = ".";

    [JsonPropertyName("solutions")]
    public string[] Solutions { get; init; } = [];

    [JsonPropertyName("budgets")]
    public Dictionary<string, double> Budgets { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("databases")]
    public DatabaseConfiguration[]? Databases { get; init; }

    [JsonPropertyName("services")]
    public JsonElement? Services { get; init; }

    [JsonPropertyName("verify")]
    public JsonElement? Verify { get; init; }

    [JsonPropertyName("sensitive")]
    public JsonElement? Sensitive { get; init; }

    public double GetBudgetSeconds(string tableName) => StalenessBudget.GetSeconds(tableName, Budgets);
}
