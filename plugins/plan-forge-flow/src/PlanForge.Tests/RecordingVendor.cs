using PlanForge.Vendors;

namespace PlanForge.Tests;

internal sealed class RecordingVendor(string id) : IVendor
{
    private readonly Queue<Script> _scripts = [];

    public string Id { get; } = id;

    public VendorCatalog Catalog { get; } = new([], CatalogSource.Resolved);

    public List<RecordingVendorSession> Sessions { get; } = [];

    public void Enqueue(object response, string? resumeToken = null) =>
        _scripts.Enqueue(new Script(response, resumeToken));

    public Task<VendorReadiness> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(new VendorReadiness(true, "recording vendor"));

    public Task<IVendorSession> StartAsync(RoleSpec role,
                                            Selection selection,
                                            string? resumeToken,
                                            CancellationToken ct)
    {
        if (_scripts.Count == 0) throw new InvalidOperationException($"no scripted response for {Id}");

        var script = _scripts.Dequeue();
        var session = new RecordingVendorSession(role, selection, resumeToken, script.Response,
                                                 script.ResumeToken);
        Sessions.Add(session);
        return Task.FromResult<IVendorSession>(session);
    }

    private sealed record Script(object Response, string? ResumeToken);
}

internal sealed class RecordingVendorSession : IVendorSession
{
    private readonly object _response;

    public RecordingVendorSession(RoleSpec role,
                                   Selection selection,
                                   string? startedWithResumeToken,
                                   object response,
                                   string? resumeToken)
    {
        Role = role;
        Selection = selection;
        StartedWithResumeToken = startedWithResumeToken;
        _response = response;
        ResumeToken = resumeToken;
    }

    public RoleSpec Role { get; }

    public Selection Selection { get; }

    public string? StartedWithResumeToken { get; }

    public string PromptText { get; private set; } = string.Empty;

    public IAsyncEnumerable<VendorEvent> Events => EmptyEvents();

    public bool CanResume => true;

    public string? ResumeToken { get; }

    public Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PromptText = prompt;

        // A scripted failure: what a vendor session does when the turn dies partway through.
        if (_response is Exception failure) throw failure;

        if (ReferenceEquals(schema, Schemas.Critique) && _response is Critique critique)
            return Task.FromResult((T)(object)critique);

        if (ReferenceEquals(schema, Schemas.BuildResult) && _response is BuildResult buildResult)
            return Task.FromResult((T)(object)buildResult);

        throw new InvalidOperationException($"scripted response does not match {typeof(T).Name}");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<VendorEvent> EmptyEvents()
    {
        yield break;
    }
}
