using System.Text.Json;
using PlanForgeFlow.Cli;

namespace PlanForgeFlow.OpenAI;

internal sealed record AppServerModel(string Id, IReadOnlyList<string> SupportedReasoningEfforts);

internal sealed record AppServerThread(string Id, string? Model, string? Status);

internal sealed record AppServerTurn(string Id, string Status, string? Error = null);

internal sealed record AppServerNotification(string Method, JsonElement Params);

internal static class AppServerContracts
{
    private static readonly HashSet<string> AllowedAccountTypes = new(StringComparer.Ordinal)
    {
        "apiKey", "chatgpt", "chatgptAuthTokens",
    };

    public static void ValidateAccount(JsonElement result)
    {
        if (!result.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null)
        {
            throw new CliFailure("auth", "Codex App Server has no authenticated OpenAI or ChatGPT account", 3);
        }
        if (!account.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !AllowedAccountTypes.Contains(type.GetString()!))
        {
            throw new CliFailure("auth", "Codex App Server account is not an OpenAI or ChatGPT authentication mode", 3);
        }
    }

    public static IReadOnlyList<AppServerModel> ParseModels(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new CliFailure("protocol", "model/list response is missing data", 3);
        }

        var models = new List<AppServerModel>();
        foreach (var item in data.EnumerateArray())
        {
            var id = RequiredString(item, "id", "model/list model");
            if (!item.TryGetProperty("supportedReasoningEfforts", out var efforts) || efforts.ValueKind != JsonValueKind.Array)
            {
                throw new CliFailure("protocol", $"model/list entry {id} is missing supportedReasoningEfforts", 3);
            }
            var supported = efforts.EnumerateArray()
                                   .Select(value => RequiredString(value, "reasoningEffort", $"model/list entry {id}"))
                                   .Distinct(StringComparer.Ordinal)
                                   .ToArray();
            if (supported.Length == 0) throw new CliFailure("protocol", $"model/list entry {id} has no supported reasoning efforts", 3);
            models.Add(new AppServerModel(id, supported));
        }
        return models;
    }

    public static AppServerModel SelectModel(IReadOnlyList<AppServerModel> catalog, string model, string effort)
    {
        var matches = catalog.Where(candidate => string.Equals(candidate.Id, model, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new CliFailure("model", $"OpenAI model {model} is not uniquely present in model/list", 3);
        if (!matches[0].SupportedReasoningEfforts.Contains(effort, StringComparer.Ordinal))
        {
            throw new CliFailure("model", $"OpenAI model {model} does not advertise effort {effort}", 3);
        }
        return matches[0];
    }

    public static AppServerThread ParseThread(JsonElement result, string? expectedId, string? expectedModel, bool requireStatus, bool requireModel = false)
    {
        if (!result.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object)
        {
            throw new CliFailure("protocol", "App Server response is missing thread", 3);
        }
        var id = RequiredString(thread, "id", "thread");
        if (expectedId is not null && !string.Equals(id, expectedId, StringComparison.Ordinal)) throw new CliFailure("identity", "App Server returned a different thread identity", 3);
        var provider = RequiredString(thread, "modelProvider", "thread");
        if (!string.Equals(provider, "openai", StringComparison.Ordinal)) throw new CliFailure("provider", "App Server thread modelProvider must be openai", 3);
        var returnedModel = thread.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String
                                ? modelValue.GetString()
                                : null;
        if (requireModel && string.IsNullOrWhiteSpace(returnedModel)) throw new CliFailure("protocol", "thread/start or thread/resume response is missing model", 3);
        if (expectedModel is not null && returnedModel is not null && !string.Equals(returnedModel, expectedModel, StringComparison.Ordinal))
        {
            throw new CliFailure("model", "App Server thread returned a different model", 3);
        }
        var status = ParseStatus(thread);
        if (requireStatus && status is null) throw new CliFailure("protocol", "thread/read response is missing status", 3);
        return new AppServerThread(id, returnedModel, status);
    }

    public static AppServerTurn ParseTurn(JsonElement container)
    {
        if (!container.TryGetProperty("turn", out var turn) || turn.ValueKind != JsonValueKind.Object)
        {
            throw new CliFailure("protocol", "App Server response is missing turn", 3);
        }
        var error = turn.TryGetProperty("error", out var errorValue) && errorValue.ValueKind == JsonValueKind.Object &&
                    errorValue.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                        ? message.GetString()
                        : null;
        return new AppServerTurn(RequiredString(turn, "id", "turn"), RequiredString(turn, "status", "turn"), error);
    }

    public static string? NextCursor(JsonElement result)
    {
        if (!result.TryGetProperty("nextCursor", out var cursor) || cursor.ValueKind == JsonValueKind.Null) return null;
        if (cursor.ValueKind != JsonValueKind.String) throw new CliFailure("protocol", "model/list nextCursor must be a string or null", 3);
        return cursor.GetString();
    }

    private static string? ParseStatus(JsonElement thread)
    {
        if (!thread.TryGetProperty("status", out var status)) return null;
        if (status.ValueKind != JsonValueKind.Object) throw new CliFailure("protocol", "thread status must be an object", 3);
        return RequiredString(status, "type", "thread status") switch
        {
            "notLoaded" or "idle" or "systemError" or "active" => status.GetProperty("type").GetString(),
            _ => throw new CliFailure("protocol", "thread status type is unsupported", 3),
        };
    }

    private static string RequiredString(JsonElement value, string property, string context)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
        {
            throw new CliFailure("protocol", $"{context} is missing {property}", 3);
        }
        return item.GetString()!;
    }
}
