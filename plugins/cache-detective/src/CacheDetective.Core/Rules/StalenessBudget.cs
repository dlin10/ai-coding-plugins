namespace CacheDetective.Rules;

public static class StalenessBudget
{
    public const double DefaultSeconds = 60;

    public static double GetSeconds(string tableName, IReadOnlyDictionary<string, double>? budgets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        if (budgets is null)
            return DefaultSeconds;

        if (budgets.TryGetValue(tableName, out var exact))
            return exact;

        var separator = tableName.IndexOf('.');
        if (separator > 0 && budgets.TryGetValue($"{tableName[..separator]}.*", out var schema))
            return schema;

        return DefaultSeconds;
    }
}
