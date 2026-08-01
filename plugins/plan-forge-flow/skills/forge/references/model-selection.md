# Runtime model selection

Forge never launches Codex CLI to enumerate models or reasoning efforts. The
multi-agent runtime exposed to the orchestrator is the only availability
source. The CLI receives only the normalized pair after the orchestrator has
understood and runtime-validated it.

## User prompt

Ask one role at a time in plain language:

```text
Which model and effort should the <reviewer|builder> use?
You may answer in free text, for example: “use Sol with high effort”.
```

Reviewer and builder have independent three-attempt counters. Increment the
counter for an unparseable answer, an ambiguous answer, a forbidden `ultra`
answer, or a runtime rejection. Explain the failure and remaining attempts in
the next question. After the third failure, stop without a default, approval,
materialization, or dispatch.

## Normalization

Resolve the answer against the model and effort values currently accepted by
the multi-agent spawn runtime:

1. Normalize Unicode, case, whitespace, and common separators for comparison,
   while preserving the canonical model ID in the result.
2. Match canonical IDs first. Also accept a unique runtime display-name or
   slug-token alias and common English/Russian effort names such as
   `low/низкий`, `medium/средний`, `high/высокий`, and `max/максимальный`.
3. Correct only one-character Damerau-Levenshtein typos when the correction is
   unique. Do not guess between multiple candidates.
4. Require exactly one model and one effort. Missing, conflicting, ambiguous,
   or unsupported values are invalid and must be retried.
5. Treat `ultra` and `ультра` as forbidden before spawning; never pass them to
   the runtime.

The output of a successful selection is exactly:

```json
{"model":"<canonical-runtime-model>","effort":"<canonical-effort>"}
```

Pass `model` and `reasoning_effort` to the native spawn call. Pass the same
canonical values as `--model` and `--effort` when registering the session with
`planforge`.

## Role lifecycle

- Select and spawn the reviewer before the first plan-review round. Spawn a
  fresh reviewer for every round and reuse the exact successful pair in Act 4.
- Select the builder only after the complete reviewed plan preview is visible.
  Spawn it immediately with a no-edit hold instruction so an invalid model or
  effort is discovered before approval. Keep that agent for Act 3; close it if
  the plan is revised or approval is abandoned.
- A later runtime failure after a pair has been successfully bound is an
  environment failure. Do not silently replace an approved selection.

## Acceptance scenarios

- **Canonical free text:** when the user supplies an exact runtime model ID and
  supported effort, normalize the pair and spawn with those canonical values.
- **Alias, synonym, and typo:** accept a runtime display-name/slug alias, an
  English or Russian effort synonym, or one obvious one-character typo only
  when the match is unique; pass the resolved canonical pair.
- **Unknown or ambiguous model:** do not spawn or choose a default; explain the
  failure and ask for that role again.
- **Unsupported effort or `ultra`:** reject the answer, count the failed
  attempt, and never send the forbidden effort to the runtime.
- **Three failures:** stop after the third failed attempt for one role without
  approval, materialization, fallback, or dispatch. Failures for the other role
  use an independent counter.
- **Lifecycle reuse:** bind the reviewer before the first review and reuse its
  pair in Act 4; after the final preview bind the builder in a no-edit hold
  before approval, reuse it in Act 3, and close/reselect it after a plan
  revision.
