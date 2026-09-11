---
name: ponytail-net
description: >-
  Keep C#/.NET implementations, bug fixes, refactorings, and code reviews on the
  simplest solution that delivers the whole requested behavior. Use when writing
  or changing C# code, choosing .NET abstractions or dependencies, or when the
  user asks to simplify, reduce overengineering, or says "ponytail", "simplest
  solution", "YAGNI", "do less", "упрости", or "не переусложняй". Levels: lite,
  full (default), ultra, off. Do not apply to non-coding work, and do not start
  a repository-wide audit.
argument-hint: "[lite|full|ultra|off]"
license: MIT
---

# Ponytail.net

Deliver the entire requested behavior with the least complexity a maintainer
must own afterwards. Prefer a readable implementation with fewer concepts over
a shorter expression with hidden semantics. Line count is a diagnostic, not the
goal: judge the result by its contract and its maintenance burden.

## Scope

Apply this skill to the current C#/.NET task only. It changes how a solution is
chosen, not what is permitted: a review or design request produces findings or
a design and does not authorize edits, the user's scope and the repository's
conventions still hold, and no additional permissions come with it.

## Levels

Default level: **full**. The user may pass `lite`, `full`, `ultra`, or `off` as
the invocation argument or in the request text.

| Level | Simplification within the requested scope |
| --- | --- |
| lite | Keep the existing shape. Make local simplifications and mention one materially simpler alternative when it exists. |
| full | Choose the simplest implementation that satisfies the complete contract; reuse suitable code and framework facilities. |
| ultra | Also challenge indirection in the affected flow and remove it when the task and every consumer allow. Removing a public member or changing a public signature requires every consumer to be in view; a consumer outside the repository that the code or its comments describe (a host, a console, a named package user) is not in view, so leave the member and describe the removal as a follow-up. The `public` modifier by itself describes no consumer: an unreferenced public type with no other evidence of a consumer is scaffolding that ultra removes. Removal needs the evidence described under "Establish the change boundary". |
| off | The skill is disabled for this task until the user re-enables it. |

An explicit level carries through follow-ups on the same task until the user
changes it; when the skill was applied automatically, it covers the current
request. Name the active level in each report so it survives a long session.
Do not create hooks, change model settings, or edit global instructions to keep
a level active.

Every level preserves required behavior and verification. An explicitly
requested feature is not speculative because a smaller feature would be easier
to build. A request to simplify, at any level including ultra, does not by
itself authorize dropping support for existing behavior or a public member.
An assumption about what the user intends is not evidence that a member is
unneeded; the evidence is the consumer accounting described under "Establish
the change boundary".

## Establish the change boundary

1. Identify the requested outcome, the callers involved, and the observable
   behavior that must stay intact. For a bug, connect the symptom to its cause
   before editing. Fix in a shared function only when every caller needs the
   same behavior; otherwise fix at the responsible boundary.
2. Read the project settings that constrain the code: SDK and `global.json`,
   target frameworks, language version, nullable and analyzer policy, package
   versions, and the existing build and test commands. Do not upgrade the
   stack to reach a more concise API.
3. For C# symbol questions (declarations, callers, references,
   implementations), use Roslyn MCP for the solution that owns the file,
   following the `roslyn-first` skill when the `roslyn-mcp` plugin is
   installed. Read the returned member spans for context. A wrong-solution
   answer means checking the matching server, not concluding that the symbol
   is absent.
4. When semantic tooling is unavailable or inconclusive, say so and fall back
   to targeted source reading and build output. Text search remains right for
   paths, configuration, literals, and non-C# artifacts.
5. An absence of source references does not prove a member unused. A member
   reached by name, through a string in a command map or configuration,
   reflection, or a serializer attribute, has a consumer although no C#
   reference exists: treat that string as the reference. Before removing
   anything, account for those, for public consumers, dependency injection,
   and generated code.

For routine reversible choices, take the interpretation best supported by the
request and the surrounding code, state consequential assumptions, and proceed.
Ask only when a missing decision materially changes scope or correctness and
the evidence cannot settle it; continue the independent work meanwhile.

## Choose an implementation

Once the required behavior is understood, take the first option that satisfies
the contract with no material unresolved concern:

1. Reuse an existing operation or abstraction that already owns the same
   responsibility.
2. Use the BCL, the framework, the database, or an already adopted library when
   its actual semantics and the available version fit the requirement.
3. Write a small direct implementation when reuse would couple unrelated
   concerns or hide the behavior.
4. Add a dependency or an abstraction only when it removes a concrete
   maintenance or correctness burden that the options above cannot carry.

Revisit the choice only when new evidence changes the trade-off. For new code,
consider standard facilities before custom helpers. Replacing a working
dependency is a migration: do it only when the task needs it and the
behavioral differences are covered. Reuse never requires a repository-wide
cleanup; remove only the scaffolding or dead code that this change makes
unnecessary.

## C# decisions that need care

Use only the rows relevant to the change.

| Tempting shortcut | Decision rule |
| --- | --- |
| Add a service interface, repository, factory, or options class for symmetry | Require a present responsibility or boundary. One implementation can justify an interface for a real port or contract; implementation count alone proves nothing. Preserve boundaries the project or task relies on. |
| Collapse async code or move dependencies into a static helper | Preserve cancellation propagation, exception behavior, resource ownership, and DI lifetimes. Keep `await` when a surrounding `using`, `try`, or scope must remain active until completion. |
| Replace a query with a shorter LINQ expression | Check ordering, comparer, null and empty behavior, enumeration count, and where execution happens. For EF Core, preserve translation, tracking needs, and transaction boundaries. Never run parallel operations on one `DbContext`. |
| Replace a custom cache with `IMemoryCache` | First check process scope, key isolation, expiration, invalidation, and concurrent misses. `GetOrCreateAsync` does not guarantee one factory execution per key. Use the existing cache facility only when it meets the required semantics. |
| Switch serializers or expose an EF entity to remove mapping | Preserve the external contract, including property names, null handling, converters, and which fields are exposed. A DTO or explicit mapping can be the smallest reliable boundary. |
| Use `Span<T>`, pooling, `ValueTask`, or a custom concurrent collection | Require a demonstrated constraint or a measured benefit that justifies the ownership and lifetime complexity. Keep a clear loop when it expresses the work better than a dense chain. |

Follow the local C# formatting. Do not compress signatures or fluent chains
merely to reduce the number of lines.

## Never simplify away

Whatever the level, keep:

- validation, authorization, and authentication checks at trust boundaries;
- error handling that prevents data loss or corruption, and the atomicity and
  concurrency guarantees the affected behavior depends on;
- nullable annotations, analyzer diagnostics, and warnings-as-errors settings:
  do not weaken or suppress them to shorten a patch;
- public API, wire, and persistence contracts, including serialization shape;
- logging, metrics, and tracing that operations depend on;
- existing tests, unless the requirement they assert was changed by the user
  or by evidence found outside this change. Rewriting the implementation does
  not change the required contract, and a suite that passes because a test
  was deleted together with the behavior it checked has verified nothing;
- anything the user explicitly asked for. When the user insists on the fuller
  version after a simpler one was offered, build it without re-arguing.

## Verify and finish

1. For a behavioral bug, reproduce the failure with a focused check before the
   fix when feasible, then confirm it passes. Extend the existing test suite
   for changed contracts and regression risks; cover the distinct relevant
   cases without an arbitrary one-test limit and without a parallel test
   framework.
2. For a mechanical, low-impact edit, run the applicable existing checks. Do
   not keep scratch scripts, and do not add tests that merely restate the code.
3. Use Roslyn file validation for fast feedback when available, then run the
   affected build and the relevant tests as the change and the repository
   require. File diagnostics do not replace build, analyzer, or runtime
   coverage. Use the configured test runner and its own syntax: VSTest and
   Microsoft.Testing.Platform options are not interchangeable. Confirm from
   the output that the intended tests actually ran.
4. Once the checks the change requires pass, stop verifying. Repeat or expand
   verification only for a new change, a failure, an unresolved concern, or a
   repository gate.
5. Inspect the final diff for scope and accidental changes. Fix failures the
   change caused; distinguish pre-existing or environmental failures, and
   report any verification left incomplete.

## Report

Keep the explanation proportional to the change and lead with the outcome:

```
ponytail-net (<level>): <what changed and why this shape fits>
Checked: <build and tests actually run, with the result>
Skipped: <simpler or fuller alternative not taken, and when to revisit>
Limits: <verification not done, assumptions that matter>
```

Omit the last two lines when there is nothing to put in them. Link edited files
rather than repeating whole files in chat. Give the fuller explanation when the
user asks for it. If no change is needed, say so with the evidence instead of
manufacturing a diff.

Concept inspired by [Ponytail](https://github.com/DietrichGebert/ponytail/blob/356918eba965ee1eac64bd3a7f0dd02108350de5/skills/ponytail/SKILL.md)
by DietrichGebert; the upstream MIT notice is kept in
[THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md).
