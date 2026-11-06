using System.Collections.Concurrent;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>A round's queue (ADR 0032): the last analysis's targets first, then every dependency and repeat by the rank of its assembly, a
/// dependency generated in the round that first reads it, one lock shared by every thread that drains the queue, and cancellation between
/// two of its members.</summary>
public sealed partial class RunGenerationTests
{
    [Fact]
    public void A_dependency_read_without_an_answer_is_generated_in_the_same_round_before_the_members_waiting_on_it()
    {
        var project = Project();
        var (alpha, beta) = (Candidate(project, "Alpha"), Candidate(project, "Beta"));
        var (delta, epsilon) = (Target(project, "Delta"), Target(project, "Epsilon"));
        var subject = Subject((request, dependencies, _) => Name(request) switch
        {
            "Alpha" => Seeing(request, dependencies, delta),
            "Delta" => Seeing(request, dependencies, epsilon),
            _ => Seeing(request, dependencies)
        }, ranks: new Dictionary<string, int> { ["Alpha"] = 2, ["Beta"] = 2, ["Delta"] = 1, ["Epsilon"] = 0 });

        subject.Generate(LibraryModels.BuiltIn, [alpha, beta]);

        // The targets come first. Delta, which Alpha read without an answer, follows in the same round; Epsilon, which Delta read, goes
        // before Delta's repeat, and Alpha waits for both.
        Assert.Equal(["Alpha", "Beta", "Delta", "Epsilon", "Delta", "Alpha"], Called);
        Assert.False(subject.HasWork);
        subject.EndScope();
        var entries = GeneratedModelFiles.Read(_repository, alpha.Target.Key).Entries;
        Assert.Equal(new[] { alpha.Target.MemberId, beta.Target.MemberId, delta.MemberId, epsilon.MemberId }.Order(StringComparer.Ordinal),
                     entries.Keys.Order(StringComparer.Ordinal));
        // Alpha's last generation saw Delta's last model, which saw Epsilon's.
        Assert.All(new[] { alpha.Target.MemberId, delta.MemberId }, member => Assert.True(Writes(entries[member])));
    }

    [Fact]
    public void Targets_go_first_by_delegate_and_rank_and_dependencies_follow_by_rank_not_by_id()
    {
        var project = Project();
        var (alpha, beta, gamma, delta) = (Candidate(project, "Alpha"), Candidate(project, "Beta"), Candidate(project, "Gamma"), Candidate(project, "Delta"));

        // A delegate first; then the rank; the declaration id only within one rank.
        Assert.Equal(["Gamma", "Beta", "Delta", "Alpha"],
                     Run(LibraryModels.BuiltIn, new Dictionary<string, int> { ["Alpha"] = 3, ["Beta"] = 1, ["Gamma"] = 2, ["Delta"] = 1 },
                         alpha, beta, gamma with { HandedDelegate = true }, delta));

        // Dependencies wait behind every target, whatever their rank, and go by rank among themselves: Epsilon before Beta.
        _calls.Clear();
        var other = Project("2.0.0");
        var (first, second) = (Target(other, "Beta"), Target(other, "Epsilon"));
        var subject = Subject((request, dependencies, _) => Name(request) == "Alpha" ? Seeing(request, dependencies, first, second) : Seeing(request, dependencies),
                              ranks: new Dictionary<string, int> { ["Alpha"] = 2, ["Gamma"] = 3, ["Beta"] = 1, ["Epsilon"] = 0 });
        subject.Generate(LibraryModels.BuiltIn, [Candidate(other, "Gamma"), Candidate(other, "Alpha")]);
        Assert.Equal(["Alpha", "Gamma", "Epsilon", "Beta", "Alpha"], Called);
    }

    [Fact]
    public void Cancellation_inside_the_queue_stops_the_round_and_the_next_round_starts_afresh()
    {
        var project = Project();
        var (alpha, beta, gamma) = (Candidate(project, "Alpha"), Candidate(project, "Beta"), Candidate(project, "Gamma"));
        using var cancellation = new CancellationTokenSource();
        var subject = Subject((request, _, token) =>
        {
            // Beta is cancelled in flight, after Alpha's answer was published and while Gamma waits in the queue.
            if (Name(request) == "Beta" && !cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return ModelAnswer(request);
        });

        using (WorkCancellation.Enter(cancellation.Token))
            Assert.ThrowsAny<OperationCanceledException>(() => subject.Generate(LibraryModels.BuiltIn, [alpha, beta, gamma]));

        Assert.Equal(["Alpha", "Beta"], Called);
        Assert.NotNull(subject.AnswerOf(alpha.Target));
        Assert.Null(subject.AnswerOf(beta.Target));
        Assert.Null(subject.AnswerOf(gamma.Target));

        // The next round holds neither the member the stopped round had in flight nor the one it had queued: it generates both, once each,
        // and not Alpha again.
        subject.Generate(LibraryModels.BuiltIn, [alpha, beta, gamma]);
        subject.EndScope();
        Assert.Equal(["Alpha", "Beta", "Beta", "Gamma"], Called);
        Assert.Equal(3, GeneratedModelFiles.Read(_repository, alpha.Target.Key).Entries.Count);
    }

    [Fact]
    public void Workers_sharing_the_queue_take_each_member_once_at_a_time_and_end_on_final_answers()
    {
        const int LEAVES = 20;
        const int DEPENDENCIES = 40;
        const int MEMBERS = 160;
        const int WORKERS = 6;
        var project = ManyProject(MEMBERS);
        // Targets M40..M159 read two of M20..M39, which read one of M0..M19: three ranks, and many dependencies read by several members.
        var targets = Enumerable.Range(DEPENDENCIES, MEMBERS - DEPENDENCIES).Select(index => Candidate(project, $"M{index}")).ToArray();
        var dependencies = Enumerable.Range(0, DEPENDENCIES).Select(index => Target(project, $"M{index}")).ToArray();
        var ranks = Enumerable.Range(0, MEMBERS).ToDictionary(index => $"M{index}", index => index < LEAVES ? 0 : index < DEPENDENCIES ? 1 : 2);
        var inFlight = new ConcurrentDictionary<string, byte>();
        var generations = new ConcurrentDictionary<string, int>();
        var threads = new ConcurrentDictionary<int, byte>();
        var overlapped = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<Exception>();
        var workers = new List<Thread>();
        var started = 0;
        using var together = new ManualResetEventSlim();
        RunGeneration? subject = null;
        subject = new RunGeneration(_repository, CancellationToken.None, Seconds(1000), Seconds(60), TimeProvider.System, (request, read, token) =>
        {
            var name = Name(request);
            if (!inFlight.TryAdd(name, 0))
                overlapped.Add(name);
            generations.AddOrUpdate(name, 1, (_, count) => count + 1);
            threads.TryAdd(Environment.CurrentManagedThreadId, 0);
            try
            {
                if (Interlocked.Exchange(ref started, 1) == 0)
                {
                    // The first generation starts the other workers and waits for them: they drain the queue at once, together.
                    workers.AddRange(Enumerable.Range(1, WORKERS - 1).Select(_ => new Thread(() =>
                    {
                        try
                        {
                            subject!.Drain();
                        }
                        catch (Exception error)
                        {
                            failures.Add(error);
                        }
                    })));
                    workers.ForEach(worker => worker.Start());
                    workers.ForEach(worker => worker.Join());
                }
                else
                {
                    // No worker generates until two have each taken a member, so that they drain together however the threads are scheduled.
                    if (threads.Count > 2)
                        together.Set();
                    together.Wait(TimeSpan.FromSeconds(30));
                }
                // Slow dependencies and quick targets: a target's repeat is often taken while a dependency it read is generated again.
                var index = int.Parse(name[1..]);
                Thread.SpinWait(Random.Shared.Next(index < DEPENDENCIES ? 40_000 : 1_000));
                IdentifiedGenerationTarget[] seen = index < LEAVES ? [] : index < DEPENDENCIES ? [dependencies[index % LEAVES]] :
                                                    [dependencies[LEAVES + index % LEAVES], dependencies[LEAVES + (index * 7 + 3) % LEAVES]];
                return Seeing(request, read, seen);
            }
            finally
            {
                inFlight.TryRemove(name, out _);
            }
        }, (request, _) => ranks[Name(request)]);

        subject.Generate(LibraryModels.BuiltIn, targets);
        subject.EndScope();

        Assert.Equal(WORKERS - 1, workers.Count);
        Assert.True(threads.Count > 2, $"{threads.Count} thread(s) generated");
        Assert.Empty(failures);
        Assert.Empty(overlapped);
        Assert.Equal(MEMBERS, generations.Count);
        Assert.False(subject.HasWork);
        // Every answer is final, so every one is written; and each member's last generation saw every dependency's last model.
        var entries = GeneratedModelFiles.Read(_repository, targets[0].Target.Key).Entries;
        Assert.Equal(MEMBERS, entries.Count);
        Assert.All(entries.Values, entry => Assert.True(Writes(entry), entry.Member));
    }

    [Fact]
    public void A_member_in_flight_is_not_taken_again_and_its_answer_is_queued_again_when_a_view_changed_meanwhile()
    {
        var project = Project();
        var target = Candidate(project, "Alpha");
        var (later, dependency, leaf) = (Target(project, "Beta"), Target(project, "Gamma"), Target(project, "Delta"));
        var ranks = new Dictionary<string, int> { ["Alpha"] = 2, ["Beta"] = 2, ["Gamma"] = 1, ["Delta"] = 0 };
        var calls = new ConcurrentQueue<string>();
        var inFlight = new ConcurrentDictionary<string, byte>();
        var overlapped = new ConcurrentBag<string>();
        using var alphaTaken = new ManualResetEventSlim();
        using var alphaReleased = new ManualResetEventSlim();
        Thread? second = null;
        RunGeneration? subject = null;
        subject = new RunGeneration(_repository, CancellationToken.None, Seconds(1000), Seconds(60), TimeProvider.System, (request, read, token) =>
        {
            var name = Name(request);
            calls.Enqueue(name);
            if (!inFlight.TryAdd(name, 0))
            {
                overlapped.Add(name);
                return Seeing(request, read);
            }
            try
            {
                var alphaCalls = calls.Count(call => call == "Alpha");
                switch (name)
                {
                    case "Alpha" when alphaCalls == 2:
                        // The second worker generates Alpha's repeat from Gamma's first model and holds it in flight.
                        var answer = Seeing(request, read, dependency, later);
                        alphaTaken.Set();
                        alphaReleased.Wait(TimeSpan.FromSeconds(30));
                        return answer;
                    case "Alpha":
                        return Seeing(request, read, dependency, later);
                    case "Gamma":
                        return Seeing(request, read, leaf);
                    case "Delta":
                        // While Delta is generated, a second worker takes Alpha's repeat, the first in the queue.
                        second = new Thread(() => subject!.Drain());
                        second.Start();
                        alphaTaken.Wait(TimeSpan.FromSeconds(30));
                        return Seeing(request, read);
                    default:
                        // Beta, last at rank 2, waits for the second worker, so the round does not end before it.
                        alphaReleased.Set();
                        second!.Join();
                        return Seeing(request, read);
                }
            }
            finally
            {
                inFlight.TryRemove(name, out _);
            }
        }, (request, _) => ranks[Name(request)]);

        subject.Generate(LibraryModels.BuiltIn, [target]);
        subject.EndScope();

        // Gamma's repeat changed what Alpha's repeat had read while Alpha was in flight: Alpha was not taken a second time. Its answer, once
        // published, went back to the queue at once and was generated from Gamma's last model; Beta's answer then asked for one more.
        Assert.Empty(overlapped);
        Assert.Equal(["Alpha", "Gamma", "Delta", "Alpha", "Gamma", "Beta", "Alpha", "Alpha"], calls);
        var entries = GeneratedModelFiles.Read(_repository, target.Target.Key).Entries;
        Assert.True(Writes(entries[target.Target.MemberId]));
        Assert.Equal(4, entries.Count);
    }

    /// <summary>A member's generation target in a scope's compilation, for a member that is a dependency rather than a scope target.</summary>
    /// <param name="compilation">The scope's compilation.</param>
    /// <param name="member">The member's name.</param>
    private GenerationTarget Target(Compilation compilation, string member) => Assert.IsType<GenerationTarget>(Candidate(compilation, member).Target);

    /// <summary>A model answer that reports one view of each dependency as the run's answers give it. Its effect is a write when it saw a
    /// model of every dependency that writes, and a deep read otherwise, so that its bytes follow what it saw.</summary>
    /// <param name="request">The member's request.</param>
    /// <param name="dependencies">The run's raw dependency answers.</param>
    /// <param name="seen">The dependencies the generation reads.</param>
    private static GeneratedAnswer Seeing(GenerationRequest request, DependencyAnswers dependencies, params IdentifiedGenerationTarget[] seen)
    {
        var views = seen.Select(target =>
        {
            var entry = dependencies(target.MemberId, target.Key);
            return new DependencyView(target, entry?.Model is not null ? DependencyViewKind.Model : entry?.Refusal is not null ? DependencyViewKind.Refusal
                                                                                                                                 : DependencyViewKind.NoAnswer, entry);
        }).ToArray();
        var writes = views.All(view => view.Entry?.Model is { } model && model.Effects.Any(effect => effect.Kind == LibraryEffectKind.WriteArgument));
        var model = new LibraryModel(request.MemberId, [new SupportedAssemblyVersion(request.AssemblyName, new Version(1, 0, 0, 0), new Version(1, 0, 0, 1))],
                                     [writes ? LibraryEffect.WriteOf("callback") : LibraryEffect.DeepReadOf("callback")]);
        return ModelAnswer(request) with { Model = model, DependencyViews = views };
    }

    /// <summary>Whether a stored entry is a model that writes, as <see cref="Seeing"/> answers after it saw every dependency's last model.</summary>
    /// <param name="entry">The entry.</param>
    private static bool Writes(GeneratedModelEntry entry) => entry.Model?.Effects.Any(effect => effect.Kind == LibraryEffectKind.WriteArgument) == true;

    /// <summary>A scope that references a package of its own whose <c>Acme.Api</c> has the members <c>M0</c> to <c>M&lt;count - 1&gt;</c>.</summary>
    /// <param name="count">The number of members.</param>
    private Compilation ManyProject(int count)
    {
        const string MANY = "Acme.Many";
        var members = string.Concat(Enumerable.Range(0, count).Select(index => $"public static void M{index}(System.Action callback) {{ }} "));
        var image = EmittedAssemblies.Image(MANY, ReferenceScopes.VERSION_ATTRIBUTE + $"namespace Acme {{ public static class Api {{ {members} }} }}");
        ReferenceScopes.AddAsset(_install, MANY, "1.0.0", "net8.0", MANY, image);
        return ReferenceScopes.Compile("public static class App { }", [.. ReferenceScopes.Runtime(_install, 8),
                                                                       ReferenceScopes.Reference(image, ReferenceScopes.PackagePath(_install, MANY, "1.0.0", "lib", "net8.0", MANY))]);
    }
}
