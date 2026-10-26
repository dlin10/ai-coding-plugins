# The run generates in rounds, and a generation takes its dependencies' built-in and generated models

This decision replaces two rules of the [ADR 0012](0012-library-semantics-are-data-the-analysis-derives-from-decompiled-code.md)
amendment of 2026-10-17, "the run generates the members it reached": that the targets are the direct opaque calls of the
analysis without the generated layer, analysed again once, and that inside a generation a call of another library's member
without a model stays opaque.

## Context

A run analyses a scope without the generated layer, takes the library members its direct opaque calls reach as targets,
answers them from the generated files or by generating them, and analyses the scope once more with those answers. Two
things stay out of reach.

A member reached only through a model is neither generated nor applied. In the program below the model of `Same` says it
calls `Equals` on its argument; the second analysis reaches the override and `Poke`, and the run lists `Poke` with the cause
`reached-through-model`. A run that only reads the files does the same: the layer holds the files of the first analysis's
targets, and an answer stored for `Poke` is applied only by the accident of sharing a declaring type with one of them.

```csharp
Acme.Api.Same(new Key(), _state);

public sealed class Key
{
    public override bool Equals(object? other) { Acme.Api.Poke(other!); return true; }
    public override int GetHashCode() => 1;
}
```

Inside a generation, the analysis of the library and its driver sees the built-in models of other assemblies and nothing
else. A call into another library that no built-in model describes is opaque, and a probe handed to it refuses the member
as `unknown-touch`. Regenerating the demo's 39 refused members other than `engine-recognized` showed six whose causes name
an unseen callee in another library outside `System.Private.CoreLib`, all in ASP.NET Core hosting: `WebApplication`'s
`CreateBuilder`, `Run` and `Services`, `WebApplicationBuilder.Build`, `Host.CreateApplicationBuilder` and
`HostApplicationBuilder.Build`, calling into `Microsoft.Extensions.Hosting`, `Microsoft.AspNetCore.Http`,
`Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Configuration.CommandLine`. Each of them
has other causes as well. The refusals of Polly name `ArgumentNullException.ThrowIfNull`, a CoreLib member.

The forces:

- A library compilation references implementation assets: a package's `lib/` folder and the shared framework's
  implementation directory, each dependency at the lowest version on disk that satisfies the ranges reaching it, not the
  version the program restores. Today a member of such a reference is no generation target at all.
- A stored answer records nothing about the models its generation used. The generator version
  ([ADR 0023](0023-a-generated-file-names-the-fingerprint-of-the-sources-its-generator-was-built-from.md)) covers the
  built-in models, which are embedded in the engine, but neither the project models nor the generated files.
- A file keeps nothing that depends on the run or the machine, and one file serves every solution of the repository.
- The time of the scope's analyses is outside the **Generation budget**, which counts time inside the generator only.
  On eShop the last measurement of a run with generation took 966.9 s, 900.0 s of it in generation, and answered 414
  members, leaving 63 to the budget.

## Decision

- **Rounds.** After the analysis without the generated layer, the run works in rounds. A round gives an answer to every
  target of the last analysis that has none and to every dependency member the last round's generations named: the
  stored answer when its file has one, otherwise a generation within the budget. The scope is analysed again with every
  answer so far when the answers the layer applies to the scope's targets differ from those the last analysis read: a
  new model, a model with other bytes, or a model replaced by a refusal. A refusal where the call was opaque changes no
  analysis and asks for none. The rounds end when no work is left — no target without an answer that a file or the budget
  can still give, no member waiting to be generated again — and the layer applies what the last analysis read; the
  finite set of members and the budget guarantee it. A round answers all its new targets at once, so the number of rounds
  follows the depth of the chains, not the number of members. A run that only reads the files makes the same rounds over
  the stored answers.
- The scope's result is its last analysis. The pairs a generated model removed are the pairs of the analysis without the
  layer that the last analysis does not have. A target left without an answer is `budget` when the run generates and
  `not-generated` when it only reads; `reached-through-model` is no longer a cause.
- **Dependencies.** Inside a generation, a call of a member of another assembly, other than `System.Private.CoreLib`,
  takes that member's built-in model, otherwise its generated answer, stored or given earlier in the run. A generation
  never reads a project model or the model lock: project entries apply to the program's own calls. A member of a library
  compilation's implementation reference is a target with its own file key, for the version that compilation references.
- A generation reports what it saw of each dependency it called: no answer, a refusal, an implementation not on disk,
  or a model by its bytes. The dependencies it saw without an answer become targets of the next round. A member's answer
  is final when its file keeps the outcome and every dependency it saw now has a final answer that looks the same to it
  — a refusal looks like no answer, a model like its bytes — and only a final answer is written. An answer that rests on
  a reason of the run — a dependency cut by the budget, one that failed or stopped, one whose implementation is not on
  disk and whose file has no answer for it — is never final in that run. A stored answer is final whether or not the
  implementation is on disk, as it is for a call of the program: a missing implementation only forbids generating it: it applies there and is not kept. Finality is worked out again along the chains at
  the end of every round, until it stops changing, so a member whose dependencies were all refused is final with its
  first answer. A member is generated again when a dependency it saw now looks different to it: a model where it saw
  none, or other bytes. Members whose views lead back to themselves, a cycle that assembly references do not allow but
  nothing else forbids, are never final and are not generated again for each other. A member cut by its own limit has no answer of the generator to read; its `timeout` is kept as
  before and is not generated again.
- The `generate` command and the model evals keep the built-in models only: they have neither a run nor a repository.
- Coverage lists the targets of the scope's own calls, as before. A dependency member generated for another member is
  written to its file, and its time is part of the generation step.

Rejected: one more round after the second analysis. The limit moves one level down and keeps its cause. Rejected: taking
the targets from the analysis with the stored layer and generating once, so that the layer converges over runs. A cold
and a warm run of the same code would report different findings until the files fill. Rejected: generating a dependency
inside the generator, recursively. The dependency would spend the member's 120 s or need limits of its own, the model
evals would move with every dependency, and the `generate` command would produce answers it has nowhere to keep.
Rejected: using only the answers that exist. An answer computed before its dependency was answered would be kept for the
whole generator version, and the result would depend on the order of generation. Rejected: giving a generation the
project models. A stored answer would then depend on files the team edits at any time: it would need a record of the
models it used, a third schema of the generated file and a rule for when it goes stale, and resolving project patterns
in a library compilation would add lock rows for dependency versions the program never references. Rejected: letting a
project entry make a dependency unavailable to generation. The member that calls it would be generated again in every
run for as long as the entry exists. Rejected: binding a dependency to the version the program restores. The answer of a
member would depend on the solution, while one file serves the whole repository.

## Consequences

- A stored answer still depends only on the libraries and the generator version, and the generated file keeps its
  schema.
- A project `opaque` entry on a dependency does not reach a member whose generated model relied on that dependency's
  generated model. A team that distrusts such a model writes a project entry for the member as well.
- Files for dependency versions the program does not reference can appear under `generated/`.
- A member with a dependency that gets a model is generated twice in the run that answers the dependency.
- The rounds' analyses are not part of the generation budget, and nothing but the new answers bounds their number. The
  first measurement of these rounds on eShop is recorded with the run that builds this decision.
- `generate` and the model evals show a member as a generation with its dependencies opaque, unless a built-in model
  describes them; a run can answer the same member differently.
