---
name: ponytail-net
description: >-
  Keep C#/.NET implementations, bug fixes, refactorings, and code reviews focused
  on the simplest complete solution. Use when changing C# code or choosing .NET
  abstractions and dependencies, especially when asked to simplify or reduce
  overengineering. Do not apply to non-coding work or initiate a repository audit.
license: MIT
metadata:
  status: draft
  target-models: Claude Fable 5.1; GPT-6-Astra
---

# Ponytail.net

Optimize for the least complexity a maintainer must own while delivering the
entire requested behavior. Prefer a readable implementation with fewer concepts
over a shorter expression with hidden semantics. Use line count only as a
diagnostic; judge the result by the contract and maintenance burden.

## Scope and level

Apply to the current C#/.NET task. A review or design request produces findings
or a design; it does not authorize code edits. Follow the user's scope and the
repository's conventions. This skill grants no additional permissions.

Default level: **full**. The user may choose `lite`, `full`, `ultra`, or `off`.
An explicit choice carries through follow-ups on the same task until changed;
automatic selection applies to the relevant request. `off` disables the skill
for this task until the user re-enables it. Do not create persistence hooks,
change model settings, or modify global instructions to maintain a level.

| Level | Simplification within the requested scope |
| --- | --- |
| lite | Keep the existing shape; make local simplifications and mention a materially simpler alternative when useful. |
| full | Choose the simplest implementation that satisfies the complete contract; reuse suitable code and framework facilities. |
| ultra | Also challenge indirection in the affected flow and remove it when compatible with the task and callers. If removal needs a broader refactor, describe it as a follow-up. |

Every level preserves required behavior and verification. An explicit feature
is not speculative merely because a smaller feature would be easier to build.

## Establish the change boundary

- Identify the requested outcome, the relevant callers, and the observable
  behavior that must remain intact. For a bug, connect the symptom to a cause
  before editing. A shared fix is appropriate only when its callers require
  the same behavior; otherwise fix the responsible boundary.
- Read the relevant project settings: SDK, target frameworks, language version,
  nullable/analyzer policy, package versions, and existing test commands. Use
  `global.json`, project files, and inherited build/package settings as needed.
  Do not upgrade the stack just to use a more concise API.
- For C# symbol questions, discover Roslyn MCP if needed and use the server for
  the owning solution first. Query declarations, callers, references, or symbol
  information as appropriate; read the returned member spans for context.
  Use absolute paths and precise positions. A wrong-solution response requires
  checking the matching server, not assuming the symbol is absent.
- If semantic tooling is unavailable or inconclusive, state that limitation
  and use targeted source inspection and build results. Text search remains
  appropriate for paths, configuration, literals, and non-C# artifacts.
  An absence of source references does not prove a member unused: account for
  public consumers, reflection, DI, serialization, and generated code before removal.

For routine reversible choices, use the interpretation best supported by the
request and surrounding code, state consequential assumptions, and proceed.
Ask only when the missing decision materially changes scope or correctness and
cannot be resolved from available evidence; continue independent work meanwhile.

## Choose an implementation

Use this preference order once the required behavior is understood:

1. Reuse an existing operation or abstraction that owns the same responsibility.
2. Use the BCL, framework, database, or already adopted library when its actual
   semantics and available version fit the requirement.
3. Write a small direct implementation when reuse would couple unrelated
   concerns or conceal the behavior.
4. Add a dependency or abstraction when it removes a concrete maintenance or
   correctness burden that the preceding options cannot reasonably handle.

Once an option satisfies the contract with no material unresolved concern,
implement it. Revisit the choice when new evidence changes the trade-off.

For new code, consider standard facilities before custom helpers. Replacing a
working dependency is a migration: do it only when the task needs it and the
behavioral differences are covered. Reuse does not require a repository-wide
cleanup. Remove only scaffolding or dead code made unnecessary by this change.

## C# decisions that need care

Use only the rows relevant to the change.

| Tempting shortcut | Decision rule |
| --- | --- |
| Add a service interface, repository, factory, or options class for symmetry | Require a present responsibility or boundary. One implementation can justify an interface for a real port or contract; implementation count alone proves nothing. Preserve boundaries the project or task relies on. |
| Collapse async code or move dependencies into a static helper | Preserve cancellation propagation, exception behavior, resource ownership, and DI lifetimes. Keep `await` when a surrounding `using`, `try`, or scope must remain active until completion. |
| Replace a query with a shorter LINQ expression | Check ordering, comparer, null/empty behavior, enumeration count, and where execution happens. For EF Core, preserve translation, tracking needs, and transaction boundaries. Do not run parallel operations on one `DbContext`. |
| Replace a custom cache with `IMemoryCache` | First check process scope, key isolation, expiration, invalidation, and concurrent misses. `GetOrCreateAsync` does not guarantee one factory execution per key. Use the existing cache facility only when it meets the required semantics. |
| Switch serializers or expose an EF entity to remove mapping | Preserve the external contract, including property names, null handling, converters, and exposed fields. A DTO or explicit mapping can be the smallest reliable boundary. |
| Use `Span<T>`, pooling, `ValueTask`, or a custom concurrent collection | Require a demonstrated constraint or measured benefit that justifies ownership and lifetime complexity. Keep a clear loop when it expresses the work better than a dense chain. |

Keep validation, authorization, atomicity, and concurrency guarantees required
by the affected behavior. Do not weaken nullable checks or suppress diagnostics
to shorten a patch. Follow local C# formatting; do not compress signatures or
fluent chains merely to reduce the number of lines.

## Calibrate frontier-model work

- **Claude Fable 5.1:** prefer patches for localized changes, batch independent
  reads when the tools support it, and provide brief progress updates during
  sustained work. Keep incidental improvements outside the patch and finish
  every requested part before reporting completion.
- **GPT-6-Astra:** make routine authorized decisions without another approval
  round. After relevant checks pass, avoid expanding or repeating verification
  unless a new change, failure, unresolved concern, or repository gate calls
  for it. Keep the final explanation proportional to the change.

These prompt calibration choices need evaluation on real tasks. Use the selected
model and effort setting. For agent delegation, follow the host's and user's
policy independently of tool batching.

## Verify and finish

- For a behavioral bug, reproduce the failure with a focused check before the
  fix when feasible, then verify it succeeds. Extend the existing test suite
  for changed contracts and regression risks; cover distinct relevant cases
  without an arbitrary one-test limit or a parallel test framework.
- For a mechanical, low-impact edit, use the applicable existing checks. Do not
  preserve scratch scripts or create tests that merely restate the code.
- Use Roslyn file validation for fast feedback when available. Then run the
  affected build and relevant tests as required by the change and repository.
  File diagnostics do not replace build, analyzer, or runtime coverage. Use the
  configured test runner and its syntax; VSTest and Microsoft.Testing.Platform
  options are not interchangeable. Verify the intended tests actually ran.
- Inspect the final diff for scope and accidental changes. If a check fails,
  investigate and fix failures caused by the change; distinguish pre-existing
  or environmental failures and report any verification left incomplete.
- Report the result, why the implementation fits, what was actually checked,
  and any material limitation. Link edited files instead of repeating whole
  files in chat. Provide a fuller explanation when requested. If no change is
  needed, explain the evidence without manufacturing a diff.

Concept inspired by [Ponytail](https://github.com/DietrichGebert/ponytail/blob/356918eba965ee1eac64bd3a7f0dd02108350de5/skills/ponytail/SKILL.md)
by DietrichGebert. Upstream attribution is retained in [LICENSE](LICENSE).
