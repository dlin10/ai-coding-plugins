# ponytail-net

A C#/.NET simplification skill for Claude Code, Codex, and Cursor. It keeps implementations, bug fixes, refactorings, and reviews on the simplest solution that still delivers the whole requested behavior: reuse what the codebase and the framework already provide, add new code only where reuse would hide behavior, and add a dependency or an abstraction only when it removes a concrete burden.

The concept comes from DietrichGebert's [Ponytail](https://github.com/DietrichGebert/ponytail) ("the laziest solution that actually works"). This plugin keeps the idea and the `lite`/`full`/`ultra` levels, and replaces the generic rules with C#-specific ones: the shortcuts that shorten a .NET patch and quietly change its contract — collapsed `await`s, LINQ rewrites over EF Core, `IMemoryCache` in place of a cache with different semantics, an entity exposed instead of a DTO — each get a decision rule, and the change is finished with the build and tests it actually requires.

## Levels

| Level | Simplification within the requested scope |
|---|---|
| `lite` | Keep the existing shape; local simplifications only, plus one materially simpler alternative named when it exists. |
| `full` (default) | The simplest implementation that satisfies the complete contract; reuse suitable code and framework facilities. |
| `ultra` | Also challenge indirection in the affected flow and remove it when the task and every consumer allow, with reference evidence; broader refactors become follow-ups. |
| `off` | Disabled for this task until re-enabled. |

An explicit level carries through follow-ups on the same task. The skill restates the active level at the top of every report, so it does not drift in a long session. Every level keeps required behavior and verification; a requested feature is never "speculative" because a smaller one would be easier.

## Invocation

| Host | How |
|---|---|
| Claude Code | `/ponytail-net:ponytail-net [lite\|full\|ultra\|off]`, or automatically when a request asks to simplify C# code |
| Codex | `$ponytail-net` with the level in the request text |
| Cursor | the `ponytail-net` skill, by name or automatically |

The skill also triggers on phrases such as "ponytail", "simplest solution", "YAGNI", "do less", "упрости", and "не переусложняй". It does not apply to non-coding requests and never starts a repository-wide audit on its own.

## What it will not do

Whatever the level, the skill keeps validation and authorization at trust boundaries, error handling that prevents data loss, atomicity and concurrency guarantees, nullable and analyzer settings, public and wire contracts, operational logging and metrics, existing tests whose requirement has not changed independently of the change, and anything the user explicitly asked for. A request to simplify, `ultra` included, is not permission to drop existing behavior or a public member on an assumed intent. A review or design request produces findings or a design, not edits, and the skill grants no permissions of its own.

## Requirements

None. Install the [roslyn-mcp](../roslyn-mcp) plugin alongside it to give the skill compiler-level evidence for callers, references, and dead-code decisions; without it the skill falls back to targeted source reading and build output, and says so.

## One skill file for three hosts

All three host manifests point at the same `skills/ponytail-net/SKILL.md`, and the skill body contains nothing host-specific: invocation syntax is documented in the table above, and reaching Roslyn MCP tools in each host is the `roslyn-first` skill's job when the [roslyn-mcp](../roslyn-mcp) plugin is installed. If a host ever needs different rules, the intended shape is a `hosts/<host>.md` reference file read on demand, not a second copy of the skill.

The skill deliberately contains no model-specific calibration. Rules that used to be addressed to a named model — decide routine authorized choices without another approval round, stop verifying once the required checks pass, keep the explanation proportional — are stated once as universal rules, and the guardrails that a less capable model needs (an explicit never-remove list, an evidence gate on `ultra` removals, a fixed report shape) cost a stronger model nothing.

## Evals

`skills/ponytail-net/evals/evals.json` holds four scenarios against small .NET 10 solutions (`evals/fixtures/setup.sh` materialises them): a bug shared by two callers, a cache added over an existing `IMemoryCache`, an `ultra` simplification with a reflection-invoked member and an out-of-repository console as the trap, and a review that must not edit. Every assertion carries a category, and results are reported per category so a better report shape never reads as better code:

| Category | What it measures |
|---|---|
| correctness | hidden contract tests pass and the requested change is made |
| scope | no new abstractions, no unrelated edits, no deleted tests, public entry points kept |
| reporting | the level line, the checks actually run, the caveats and reasons |

The comparison that matters is the skill against no skill at all, with several runs per scenario; an older draft of the skill is not a baseline.

## Attribution

Concept inspired by [Ponytail](https://github.com/DietrichGebert/ponytail/blob/356918eba965ee1eac64bd3a7f0dd02108350de5/skills/ponytail/SKILL.md) by DietrichGebert, MIT. The upstream notice is kept in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); this plugin is released under the [MIT License](LICENSE).
