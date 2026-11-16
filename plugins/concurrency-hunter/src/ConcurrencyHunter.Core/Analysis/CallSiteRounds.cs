using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

/// <summary>The growing admission of one scope. Each proposal is counted through reachability before any summary or heap is built.</summary>
/// <param name="deciding">Classifies the latest deciding calls and their producers.</param>
/// <param name="projects">The program's compilations.</param>
/// <param name="count">Runs reachability without building summaries or the heap.</param>
/// <param name="bodyBound">A smaller bound supplied by tests, or the measured product bound.</param>
internal sealed class CallSiteRounds(DecidingCalls deciding, IReadOnlyList<Compilation> projects,
                                      Func<CallSiteAdmission, IReadOnlyList<Compilation>, ScopeRun> count, int? bodyBound = null)
{
    private const int CALL_SITE_BODY_BOUND = 1500;
    private readonly Dictionary<CallIdentity, string> _admitted = [];
    private readonly Dictionary<string, Compilation> _libraries = new(StringComparer.Ordinal);
    private readonly List<CallSiteCallMeasurement> _measurements = [];
    private readonly Dictionary<CallIdentity, ObservedCall> _calls = [];

    private sealed record ObservedCall(CallSiteTarget Target, IReadOnlyList<CallIdentity> Producers, int Bodies = 0, bool OverBound = false);

    internal IReadOnlyList<CallSiteCallMeasurement> Measurements => _measurements;
    internal CallSiteAdmission Admission => new(_admitted.ToDictionary());
    internal IReadOnlyList<Compilation> Libraries => _libraries.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray();

    internal void Observe(ScopeRun run, PairAnalysis pairs)
    {
        foreach (var target in deciding.Of(run, pairs, projects))
        {
            WorkCancellation.Check();
            var producers = ProducerCalls.Of(run.Heap!, target.Call, IsProgramBody);
            if (_calls.TryGetValue(target.Call, out var earlier))
                _calls[target.Call] = earlier with { Producers = earlier.Producers.Concat(producers).Distinct().ToArray() };
            else
                _calls.Add(target.Call, new(target, producers.ToArray()));
        }
    }

    internal CallSiteCoverage Coverage(int analyses, ScopeRun run, PairAnalysis pairs, CallSiteAdmission? admission,
                                       IReadOnlySet<string> layerPairs)
    {
        Observe(run, pairs);
        var lastCalls = pairs.Pairs.SelectMany(pair => pair.DecidingCalls).Select(decision => decision.Call).OfType<CallIdentity>().ToHashSet();
        var lastPairs = pairs.Pairs.Select(PairIdentity.Of).ToHashSet(StringComparer.Ordinal);
        var calls = _calls.Values.OrderBy(call => call.Target.Callee, StringComparer.Ordinal)
                          .ThenBy(call => call.Target.Call.Span.Path, StringComparer.Ordinal).ThenBy(call => call.Target.Call.Span.StartLine)
                          .ThenBy(call => call.Target.Call.Span.StartColumn).ThenBy(call => call.Target.Call.Span.EndLine)
                          .ThenBy(call => call.Target.Call.Span.EndColumn).ThenBy(call => call.Target.Call.Ordinal)
                          .ThenBy(call => call.Target.Call.BodyId, StringComparer.Ordinal).ThenBy(call => call.Target.Call.Target, StringComparer.Ordinal)
                          .Select(call => new CallSiteCallCoverage(call.Target.Callee, Place(call.Target.Call, run, admission),
                              call.Producers.OrderBy(producer => producer.Span.Path, StringComparer.Ordinal).ThenBy(producer => producer.Span.StartLine)
                                  .ThenBy(producer => producer.Span.StartColumn).ThenBy(producer => producer.Span.EndLine)
                                  .ThenBy(producer => producer.Span.EndColumn).ThenBy(producer => producer.Ordinal)
                                  .ThenBy(producer => producer.BodyId, StringComparer.Ordinal).ThenBy(producer => producer.Target, StringComparer.Ordinal)
                                  .Select(producer => Place(producer, run, admission)).ToArray(), call.Bodies,
                              CallSiteOutcomes.Of(call.Target.Outcome,
                                  admission?.Calls is { } admitted ? admitted.ContainsKey(call.Target.Call) : admission?.BodyIds.Contains(call.Target.Call.Target) == true,
                                  lastCalls.Contains(call.Target.Call), call.OverBound))).ToArray();
        return new(analyses, calls, layerPairs.Except(lastPairs).Count(), lastPairs.Except(layerPairs).Count());
    }

    internal bool Grow(ScopeRun run, PairAnalysis pairs, int round)
    {
        Observe(run, pairs);
        var additions = new Dictionary<CallIdentity, string>();
        foreach (var call in deciding.Of(run, pairs, projects).Where(call => call.Outcome is null))
        {
            WorkCancellation.Check();
            if (_admitted.ContainsKey(call.Call))
                continue;
            var producers = ProducerCalls.Of(run.Heap!, call.Call, IsProgramBody)
                                         .Select(producer => deciding.Target(run, producer, projects)).ToArray();
            var entries = producers.Prepend(call).Where(target => target.Outcome is null && target.Library is not null)
                                   .DistinctBy(target => target.Call).ToArray();
            var admission = new CallSiteAdmission(entries.ToDictionary(target => target.Call, target => target.Call.Target));
            var libraries = entries.Select(target => target.Library!).Distinct().ToArray();
            var reached = count(admission, libraries).Reachable;
            var bodies = reached.ReachedBodies.Keys.Count(admission.IsLibraryBody);
            _calls[call.Call] = _calls[call.Call] with { Bodies = bodies, OverBound = bodies > (bodyBound ?? CALL_SITE_BODY_BOUND) };
            if (bodies > (bodyBound ?? CALL_SITE_BODY_BOUND))
                continue;
            foreach (var entry in entries)
            {
                WorkCancellation.Check();
                additions[entry.Call] = entry.Call.Target;
                _libraries[entry.Library!.AssemblyName!] = entry.Library;
            }
            _measurements.Add(new(call.Callee, Place(call.Call, run), producers.Select(producer => Place(producer.Call, run)).Distinct().Order(StringComparer.Ordinal).ToArray(),
                                  bodies, round));
        }
        foreach (var (call, target) in additions)
        {
            WorkCancellation.Check();
            _admitted.TryAdd(call, target);
        }
        return additions.Count != 0;
    }

    internal IReadOnlyList<CallSiteCallMeasurement> MeasureAssemblyWide(ScopeRun run, CallSiteAdmission admission,
                                                                       IReadOnlyList<Compilation> libraries)
    {
        var calls = run.Reachable.Bodies.Values.Where(body => IsProgramBody(body.BodyId))
            .SelectMany(body => body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                .Where(call => call.CallKind is not (IrCallKind.Virtual or IrCallKind.Interface or IrCallKind.Delegate) &&
                               call.TargetMethodId is { } target && admission.BodyIds.Contains(target))
                .Select(call => body.CallIdentities[call.Id])).Distinct().ToArray();
        var measurements = new List<CallSiteCallMeasurement>();
        foreach (var call in calls)
        {
            WorkCancellation.Check();
            var producers = ProducerCalls.Of(run.Heap!, call, IsProgramBody);
            var entries = calls.Where(candidate => candidate == call || producers.Contains(candidate))
                               .ToDictionary(candidate => candidate, candidate => candidate.Target);
            var reached = count(new CallSiteAdmission(entries), libraries).Reachable;
            measurements.Add(new(call.Target, Place(call, run, admission), producers.Select(producer => Place(producer, run, admission)).Distinct().Order(StringComparer.Ordinal).ToArray(),
                                 reached.ReachedBodies.Keys.Count(admission.IsLibraryBody), 1));
        }
        return measurements;
    }

    private bool IsProgramBody(string bodyId) => projects.Any(project => bodyId.StartsWith($"body:{project.AssemblyName}:", StringComparison.Ordinal));
    private static string Place(CallIdentity call, ScopeRun run, CallSiteAdmission? admission = null) =>
        CoveragePlace.Of(call.BodyId, call.Span, run.Program, admission).ToString();
}
