using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Advisors;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Run;

namespace PlanForge.Tests;

/// <summary>One item in one fixture plan: its candidates, the fake Advisor's script and the semantic truth.</summary>
/// <param name="Id">The stable item identity.</param>
/// <param name="Text">The exact text used to look up scripted answers.</param>
/// <param name="Candidates">The expected candidate unit ids, selected by code without any model.</param>
/// <param name="Script">What the fake Advisor answers and the status those answers settle to.</param>
/// <param name="Truth">What a correct Advisor should conclude; the live metrics measure against it.</param>
internal sealed record EvidenceLabel(string Id, string Text, string[] Candidates, EvidenceScript Script, EvidenceTruth Truth);

/// <summary>The fake Advisor's answers for one item, including refusals and abstentions written only to
/// exercise the unchecked and uncertain paths.</summary>
/// <param name="Status">The status the scripted answers settle to.</param>
/// <param name="Relevance">The scripted relevance choice, absent for implications.</param>
/// <param name="Pairs">The scripted answers for every candidate unit.</param>
internal sealed record EvidenceScript(string Status, string? Relevance, Dictionary<string, EvidencePairLabel> Pairs);

/// <summary>The semantic expectation for one item, independent of any script or model answer.</summary>
/// <param name="Status">The status a correct Advisor's answers settle to.</param>
/// <param name="Obligations">Units that explicitly commit the plan to act on this item.</param>
internal sealed record EvidenceTruth(string Status, string[] Obligations);

/// <summary>The two scripted answers for a candidate pair.</summary>
/// <param name="Relationship">A relationship choice or refused.</param>
/// <param name="Obligation">An obligation choice or refused.</param>
internal sealed record EvidencePairLabel(string Relationship, string Obligation);

/// <summary>Reads copied fixture assets and owns an isolated Run with unchanged recorded locators.</summary>
internal sealed class EvidenceMapFixture : IDisposable
{
    /// <summary>The copied fixture root, independent of the process working directory.</summary>
    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "Fixtures", "EvidenceMap");
    /// <summary>The isolated workspace containing only sanitized source.</summary>
    public string Root { get; } = Directory.CreateTempSubdirectory("planforge-labelled-evidence-").FullName;
    /// <summary>The Run used by direct mapping and tool-boundary tests.</summary>
    public RunDirectory Run { get; }
    /// <summary>The three complete Scout answers, with recorded checks read from disk.</summary>
    public IReadOnlyList<ScoutAnswerEntry> Entries { get; }
    /// <summary>The stable Change-point list used by both plans.</summary>
    public string[] ChangePoints { get; } = JsonSerializer.Deserialize(Read("change-points.json"), EvidenceFixtureJson.Default.StringArray)!;
    /// <summary>The Orchestrator's derived obligations.</summary>
    public EvidenceImplication[] Implications { get; } = JsonSerializer.Deserialize(Read("implications.json"), EvidenceIndexJson.Default.EvidenceImplicationArray)!;
    /// <summary>The independently written labels for both plans.</summary>
    public Dictionary<string, EvidenceLabel[]> Labels { get; } = JsonSerializer.Deserialize(Read("labels.json"), EvidenceFixtureJson.Default.DictionaryStringEvidenceLabelArray)!;
    /// <summary>The progress of every direct call, including calls that only read the store.</summary>
    public List<EvidenceMapProgress> Progress { get; } = [];

    /// <summary>Copies sanitized sources and writes a Run without invoking any Vendor or network.</summary>
    public EvidenceMapFixture()
    {
        var sources = Path.Combine(Assets, "repo");
        foreach (var file in Directory.EnumerateFiles(sources, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(Root, Path.GetRelativePath(sources, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            AtomicFile.Write(destination, AtomicFile.Read(file));
        }
        Run = RunDirectory.Create(Root, "fixture");
        Run.WriteState(new RunState(Run.RunId, Root, "Text", DateTimeOffset.UnixEpoch, 0, 5,
                                    BaselineHead: "fixture", ScoutAnswers: 3));
        AtomicFile.Write(Run.ScoutAnswersPath, Read("scout-answers.json"));
        Entries = ScoutAnswers.Read(AtomicFile.Read(Run.ScoutAnswersPath));
    }

    /// <summary>Reads an immutable copied fixture file without normalizing its line endings.</summary>
    /// <param name="name">The asset's relative file name.</param>
    public static string Read(string name) => AtomicFile.Read(Path.Combine(Assets, name));

    /// <summary>
    /// Maps a plan, retaining each call's progress for pooled evaluation. Each plan is its own scenario and starts from an
    /// empty store, since a complete map leaves the items it judged out of every later call.
    /// </summary>
    /// <param name="plan">The plan asset name.</param>
    /// <param name="advisor">The live or scripted Advisor, defaulting to the labels.</param>
    /// <param name="scoutAnswers">The consumed answer count, optionally including missing entries.</param>
    /// <param name="keepStore">Whether to map against the store the previous call left.</param>
    public async Task<EvidenceMap> Map(string plan, IAdvisor? advisor = null, int scoutAnswers = 3, bool keepStore = false)
    {
        if (!keepStore) File.Delete(Run.EvidenceMapPath);
        Run.WritePlan(Read(plan));
        var mapper = new EvidenceMapper();
        try
        {
            return await mapper.MapAsync(Root, Read(plan), Entries, scoutAnswers, ChangePoints,
                                         advisor ?? new LabelledAdvisor(this, plan), Run.EvidenceMapPath, Implications);
        }
        finally { Progress.Add(mapper.Progress); }
    }

    /// <summary>Deletes only this fixture's temporary workspace.</summary>
    public void Dispose()
    {
        try { Directory.Delete(Root, true); }
        catch (IOException) { }
    }
}

/// <summary>Answers only from hand-written fixture labels, using the same Choice seam as a live service.</summary>
internal sealed class LabelledAdvisor : IAdvisor
{
    /// <summary>Item text selects the label without sending local identities in requests.</summary>
    private readonly Dictionary<string, EvidenceLabel> labels;
    /// <summary>Question hashes identify exact unit text in this plan.</summary>
    private readonly Dictionary<string, string> units;
    /// <summary>The deterministic service id.</summary>
    public string Id => "labelled";
    /// <summary>The deterministic requested and served model.</summary>
    public string RequestedModel => "fixture-1";
    /// <summary>The fake's unit is question count; packing and concurrency still run in production code.</summary>
    public AdvisorLimits Limits { get; } = new("questions", 64, 1, 64, 255, 8, TimeSpan.FromSeconds(30));
    /// <summary>A scripted service costs nothing.</summary>
    public decimal InputUsdPerMillion => 0m;
    /// <summary>Every request actually dispatched, useful for invalidation assertions.</summary>
    public List<AdviceRequest> Requests { get; } = [];

    /// <summary>Loads the scripts for one plan; expected statuses never decide an answer.</summary>
    /// <param name="fixture">The shared assets and labels.</param>
    /// <param name="plan">The plan asset name.</param>
    public LabelledAdvisor(EvidenceMapFixture fixture, string plan)
    {
        labels = fixture.Labels[plan].ToDictionary(label => label.Text, StringComparer.Ordinal);
        units = PlanUnits.Of(EvidenceMapFixture.Read(plan)).ToDictionary(unit => unit.Hash, unit => unit.Id);
    }

    /// <summary>Measures the fake service's question-count unit.</summary>
    /// <param name="request">The exact request being packed.</param>
    public AdviceSize Measure(AdviceRequest request) => new(request.Questions.Count, 1);

    /// <summary>Returns normalized distributions or an explicit scripted refusal.</summary>
    /// <param name="request">The production rubric state and questions.</param>
    /// <param name="ct">The mapper's cancellation token.</param>
    public Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add(request);
        var label = labels[request.RequestState["item"]!["text"]!.GetValue<string>()];
        var answers = new Dictionary<string, AdviceOutcome>();
        foreach (var question in request.Questions)
        {
            var role = question.Id.Split(':')[0];
            var pair = role == "relevance" ? null : label.Script.Pairs[units[question.Id[(question.Id.IndexOf(':') + 1)..]]];
            var choice = role == "relevance" ? label.Script.Relevance! : role == "relationship" ? pair!.Relationship : pair!.Obligation;
            answers.Add(question.Id, choice == "refused" ? new Refused()
                : new Answered(choice, question.Options.ToDictionary(option => option.Value,
                    option => option.Value == choice ? 1d : 0d), 1));
        }
        return Task.FromResult(new AdviceResult(answers, RequestedModel));
    }
}

/// <summary>Reflection-free contracts for immutable fixture labels and the evaluation report.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, EvidenceLabel[]>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(EvidenceMetricsReport))]
internal sealed partial class EvidenceFixtureJson : JsonSerializerContext;
