using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlanForge.Vendors.Codex;

/// <summary>
/// Codex reached through its App Server rather than its CLI: a persistent connection, one thread per
/// session, one turn per call. It is the only vendor whose catalogue advertises which efforts each
/// model actually supports.
/// </summary>
internal sealed class CodexAppServerVendor : IVendor
{
    private readonly string _workspace;

    public CodexAppServerVendor(string? workingDirectory = null)
    {
        _workspace = workingDirectory is { Length: > 0 } directory ? directory : Environment.CurrentDirectory;
        Catalog = new VendorCatalog([], CatalogSource.Live);
    }

    public string Id => "codex";

    /// <summary>Filled by <see cref="ProbeAsync"/> — Codex publishes a live model list.</summary>
    public VendorCatalog Catalog { get; private set; }

    public async Task<VendorReadiness> ProbeAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = AppServerConnection.Start(_workspace);
            await connection.InitializeAsync(ct);

            if (!await IsSignedInAsync(connection, ct))
                return new VendorReadiness(false, "codex is not signed in — run 'codex login'");

            Catalog = new VendorCatalog(await ListModelsAsync(connection, ct), CatalogSource.Live);
            return new VendorReadiness(true, $"{Catalog.Models.Count} models");
        }
        catch (Exception error) when (error is VendorException or OperationCanceledException)
        {
            return new VendorReadiness(false, error.Message);
        }
    }

    public async Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct)
    {
        var connection = AppServerConnection.Start(_workspace);
        try
        {
            await connection.InitializeAsync(ct);
            var threadId = await OpenThreadAsync(connection, role, selection, resumeToken, ct);
            return new CodexAppServerSession(connection, threadId, role, selection, _workspace);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<string> OpenThreadAsync(AppServerConnection connection,
                                               RoleSpec role,
                                               Selection selection,
                                               string? resumeToken,
                                               CancellationToken ct)
    {
        var builder = role.Role is VendorRole.Builder;
        var parameters = new JsonObject
        {
            ["model"] = selection.Model,
            ["cwd"] = _workspace,
            ["approvalPolicy"] = "never",
            // thread/start spells the sandbox in kebab-case; turn/start spells the same choice in
            // camelCase. Verified against 0.147.0, which rejects the other spelling outright.
            ["sandbox"] = builder ? "workspace-write" : "read-only"
        };

        JsonElement result;
        if (resumeToken is { Length: > 0 })
        {
            parameters["threadId"] = resumeToken;
            result = await connection.RequestAsync("thread/resume", parameters, ct);
        }
        else
        {
            parameters["serviceName"] = "plan_forge_flow";
            parameters["developerInstructions"] = role.SystemPrompt;
            // Only a Builder is ever resumed, so only a Builder's thread has to outlive the process.
            parameters["ephemeral"] = !builder;
            result = await connection.RequestAsync("thread/start", parameters, ct);
        }

        if (!result.TryGetProperty("thread", out var thread)
            || !thread.TryGetProperty("id", out var id)
            || id.GetString() is not { Length: > 0 } threadId)
        {
            throw new VendorException("the Codex App Server returned no thread id");
        }

        return threadId;
    }

    private static async Task<bool> IsSignedInAsync(AppServerConnection connection, CancellationToken ct)
    {
        var result = await connection.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, ct);

        var hasAccount = result.TryGetProperty("account", out var account)
                         && account.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        var needsAuth = result.TryGetProperty("requiresOpenaiAuth", out var required)
                        && required.ValueKind is JsonValueKind.True;

        return hasAccount || !needsAuth;
    }

    private static async Task<List<VendorModel>> ListModelsAsync(AppServerConnection connection, CancellationToken ct)
    {
        var models = new List<VendorModel>();
        string? cursor = null;

        do
        {
            var parameters = new JsonObject { ["limit"] = 100, ["includeHidden"] = false };
            if (cursor is not null) parameters["cursor"] = cursor;

            var page = await connection.RequestAsync("model/list", parameters, ct);
            models.AddRange(ParseModels(page));

            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind is JsonValueKind.String
                ? next.GetString()
                : null;
        } while (cursor is not null);

        return models;
    }

    internal static List<VendorModel> ParseModels(JsonElement page)
    {
        if (!page.TryGetProperty("data", out var data) || data.ValueKind is not JsonValueKind.Array)
            throw new VendorException("model/list returned no data");

        var models = new List<VendorModel>();
        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } modelId) continue;

            // Efforts arrive as objects, each carrying the level and a description of it.
            var efforts = entry.TryGetProperty("supportedReasoningEfforts", out var supported)
                          && supported.ValueKind is JsonValueKind.Array
                ? supported.EnumerateArray()
                           .Select(effort => effort.TryGetProperty("reasoningEffort", out var level)
                                       ? level.GetString()
                                       : null)
                           .OfType<string>()
                           .ToArray()
                : [];

            models.Add(new VendorModel(modelId, efforts,
                DisplayName: OptionalString(entry, "displayName"),
                Description: OptionalString(entry, "description"),
                DefaultEffort: OptionalString(entry, "defaultReasoningEffort"),
                IsDefault: entry.TryGetProperty("isDefault", out var isDefault)
                           && isDefault.ValueKind is JsonValueKind.True));
        }

        return models;
    }

    private static string? OptionalString(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
