using System.Globalization;
using PlanForge.Vendors;

namespace PlanForge.Tests;

internal sealed class RecordingVendor(string id) : IVendor
{
    private readonly Queue<Script> _scripts = [];

    public string Id { get; } = id;

    public VendorCatalog Catalog { get; } = new([], CatalogSource.Resolved);

    public List<RecordingVendorSession> Sessions { get; } = [];

    public void Enqueue(object response, string? resumeToken = null, IReadOnlyList<string>? killedBackgroundTasks = null) =>
        _scripts.Enqueue(new Script(response, resumeToken, killedBackgroundTasks ?? []));

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
                                                 script.ResumeToken, script.KilledBackgroundTasks);
        Sessions.Add(session);
        return Task.FromResult<IVendorSession>(session);
    }

    private sealed record Script(object Response, string? ResumeToken, IReadOnlyList<string> KilledBackgroundTasks);
}

internal sealed class RecordingVendorSession : IVendorSession
{
    private readonly object _response;

    public RecordingVendorSession(RoleSpec role,
                                   Selection selection,
                                   string? startedWithResumeToken,
                                   object response,
                                   string? resumeToken,
                                   IReadOnlyList<string>? killedBackgroundTasks = null)
    {
        Role = role;
        Selection = selection;
        StartedWithResumeToken = startedWithResumeToken;
        _response = response;
        ResumeToken = resumeToken;
        KilledBackgroundTasks = killedBackgroundTasks ?? [];
    }

    public RoleSpec Role { get; }

    public Selection Selection { get; }

    public string? StartedWithResumeToken { get; }

    public string PromptText { get; private set; } = string.Empty;

    public IAsyncEnumerable<VendorEvent> Events => EmptyEvents();

    public bool CanResume => true;

    public string? ResumeToken { get; }

    public IReadOnlyList<string> KilledBackgroundTasks { get; }

    public Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PromptText = prompt;

        // A scripted failure: what a vendor session does when the turn dies partway through.
        if (_response is Exception failure) throw failure;

        if (ReferenceEquals(schema, Schemas.Critique))
        {
            if (_response is VendorCritique wireCritique)
                return Task.FromResult((T)(object)wireCritique);

            if (_response is Critique critique)
            {
                var wire = new VendorCritique
                {
                    Verdict = critique.Verdict,
                    Findings = [.. critique.Findings.Select(finding =>
                        new VendorFinding(finding.Severity, finding.Where, finding.What))],
                    Summary = critique.Summary,
                    UnresolvedAssessments = critique.UnresolvedAssessments
                        ?? [.. ProjectionIds(prompt).Select(id =>
                            new UnresolvedAssessment(id, true, "scripted assessment"))],
                    Reopenings = critique.Reopenings ?? []
                };
                return Task.FromResult((T)(object)wire);
            }
        }

        if (ReferenceEquals(schema, Schemas.BuildResult) && _response is BuildResult buildResult)
            return Task.FromResult((T)(object)buildResult);

        if (ReferenceEquals(schema, Schemas.ScoutReport) && _response is ScoutReport scoutReport)
            return Task.FromResult((T)(object)scoutReport);

        throw new InvalidOperationException($"scripted response does not match {typeof(T).Name}");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IReadOnlyList<string> ProjectionIds(string prompt)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + 6 <= prompt.Length; index++)
        {
            if (prompt[index] != 'F' || prompt[index + 1] != '-') continue;
            var candidate = prompt.Substring(index, 6);
            if (candidate.Length == 6 && int.TryParse(candidate.AsSpan(2), NumberStyles.None,
                                                       CultureInfo.InvariantCulture, out var number)
                && number > 0)
                ids.Add(candidate);
        }

        return ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    private static async IAsyncEnumerable<VendorEvent> EmptyEvents()
    {
        yield break;
    }
}
