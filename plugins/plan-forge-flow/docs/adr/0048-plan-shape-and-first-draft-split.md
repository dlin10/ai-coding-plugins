---
status: accepted
---

# Report the first draft's shape and offer a split before review

## Context

The first plan draft can already contain more design than one review loop can usefully settle. In
Concurrency Hunter Run `20261024-105628-872ce5` (B4c), the first draft had 47,528 UTF-16 characters,
8 Approach tasks, 11,044 characters of Requirements and 27,501 of Approach (2.49:1). The Run spent
10 plan-review rounds on 44 findings, 21 of them major, without reaching approval. A shape report
at `forge.plan.write` would have made the scale visible before round 1. Length alone cannot tell
whether a plan should be split, so the signal must be advisory and grounded in past first drafts.

## Decision

- `forge.plan.write` and `forge.plan.patch` return a `shape` derived from the same plan outline
  they already return. The Flow log records that shape. It includes total characters; task count,
  largest task and sum of task characters; Requirements and Approach characters and their ratio;
  distinct R-items; distinct named `*Tests` classes and the `*MatrixTests` subset. Counts are
  UTF-16 code units, matching .NET `string.Length`.
- **An Approach of at least 22,000 characters is the first-draft split signal.** After the first
  write and before the first `forge.plan.review`, the Orchestrator uses the signal to propose a
  split to the user. The proposal names separable tasks or task groups, their sizes and the
  requirements they serve, and offers review of the plan as written as an alternative. The
  Orchestrator uses the plan's actual seams, not a mechanical partition by length. It may also
  propose a split below the threshold when the plan's content calls for one.
- Shape and the signal do not refuse a write, patch, confirmation or review. A plan is never split
  automatically. A later patch can change the reported shape, but the before-round-1 offer is
  tied to the first draft, so revision growth does not repeatedly ask the same question.

## Calibration

The table covers 26 first drafts: all 16 archived runs in the 2026-10-06 plan-review study and
10 later Concurrency Hunter runs through B4c. The first 16 rows are archival; the last 10 are
transcript-sourced. Measurements are of the **first draft**, while outcomes cover the observed
plan-review run. `Rounds` means observed rounds, not necessarily rounds to approval: B4c had no
approval. A `—` under Change points means no first Impact pass value was available, not zero.

Columns: `T/max/sum` is Approach task count, largest task and sum of task characters;
`Req/App/ratio` is section characters and Approach divided by Requirements; `R` is distinct
Requirements IDs; `Tests/Matrix` is distinct named test classes and the `*MatrixTests` subset;
`CP` is Change points from the first `forge.evidence.map` call; `outcomes` is observed
rounds/findings/major findings/elapsed minutes. The plan and section counts include headings and
line breaks. Task sum excludes the Approach heading, hence its small difference from Approach.

| Run | Chars | T/max/sum | Req/App/ratio | R | Tests/Matrix | CP | Outcomes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 20260919-160244-77fd30 | 35,493 | 12/3,202/28,853 | 5,417/28,866/5.33 | 13 | 15/0 | — | 8/48/27/111 |
| 20260922-183525-7f89fb | 22,120 | 8/1,639/10,052 | 3,500/10,065/2.88 | 10 | 8/0 | — | 12/195/93/352 |
| 20260923-082443-d556ab | 190,336 | 8/1,942/11,299 | 3,950/11,312/2.86 | 10 | 14/0 | — | 2/45/20/39 |
| 20260923-111905-632662 | 18,839 | 7/1,748/7,698 | 2,970/7,711/2.60 | 8 | 9/0 | — | 7/50/36/78 |
| 20260924-133053-da283a | 31,391 | 6/1,704/7,638 | 6,398/7,651/1.20 | 8 | 5/0 | — | 5/16/9/57 |
| 20260925-162135-46c10c | 40,063 | 7/3,474/15,082 | 9,234/15,095/1.63 | 10 | 30/0 | — | 7/27/11/208 |
| 20260926-115027-22c72d | 22,547 | 5/2,385/8,138 | 6,966/8,151/1.17 | 10 | 12/1 | — | 5/32/22/73 |
| 20260927-101749-f2aa9c | 34,528 | 5/3,050/9,847 | 11,486/9,860/0.86 | 9 | 19/2 | — | 4/9/7/49 |
| 20260927-212553-b98178 | 37,084 | 6/4,488/14,714 | 9,057/14,727/1.63 | 12 | 16/0 | — | 6/59/30/75 |
| 20260928-113803-31c1d7 | 55,994 | 8/4,710/24,019 | 12,293/24,032/1.95 | 11 | 25/2 | — | 6/55/31/84 |
| 20260928-211855-673395 | 61,024 | 13/6,511/35,328 | 8,100/35,341/4.36 | 11 | 25/1 | — | 9/81/58/499 |
| 20260930-124014-84aa6c | 59,363 | 7/6,146/26,399 | 8,145/26,412/3.24 | 11 | 23/1 | — | 7/60/35/286 |
| 20261001-125229-9372c2 | 38,273 | 8/2,105/11,979 | 5,023/11,992/2.39 | 11 | 13/0 | — | 7/111/80/266 |
| 20261002-164725-76278e | 24,913 | 10/3,374/16,156 | 4,807/16,169/3.36 | 8 | 13/0 | — | 12/70/57/925 |
| 20261004-093111-b79938 | 43,808 | 10/4,919/33,166 | 6,317/33,179/5.25 | 12 | 41/2 | — | 8/115/97/244 |
| 20261005-204040-0c34b6 | 54,750 | 13/4,395/39,304 | 8,047/39,317/4.89 | 12 | 57/6 | — | 7/61/44/444 |
| 20261006-211341-82aa0e | 48,434 | 13/5,603/37,792 | 4,221/37,805/8.96 | 11 | 57/8 | — | 7/62/48/511 |
| 20261009-194113-1000db | 21,731 | 7/3,040/13,768 | 3,846/13,781/3.58 | 8 | 33/1 | — | 8/31/20/127 |
| 20261012-163837-6fc7b1 | 51,269 | 14/5,961/29,308 | 8,473/29,321/3.46 | 15 | 79/9 | — | 7/44/35/170 |
| 20261014-181155-5c1f5a | 62,980 | 13/5,214/37,494 | 9,125/37,507/4.11 | 14 | 58/7 | — | 7/51/46/138 |
| 20261017-180043-675224 | 43,463 | 14/3,984/28,278 | 8,423/28,291/3.36 | 13 | 29/1 | — | 6/66/54/166 |
| 20261019-215643-7586e3 | 29,136 | 11/2,983/22,460 | 3,693/22,473/6.09 | 10 | 22/2 | 10 | 6/55/36/305 |
| 20261020-123405-0d2a47 | 27,684 | 10/3,269/19,195 | 4,551/19,208/4.22 | 8 | 18/3 | 10 | 5/27/21/118 |
| 20261022-105845-5a51cc | 36,935 | 13/3,460/21,668 | 7,359/21,681/2.95 | 12 | 39/9 | 8 | 3/12/5/66 |
| 20261022-214038-e08c4f | 30,733 | 7/3,384/16,433 | 6,148/16,446/2.68 | 8 | 37/7 | 10 | 6/15/3/501 |
| 20261024-105628-872ce5 | 47,528 | 8/5,814/27,488 | 11,044/27,501/2.49 | 11 | 72/14 | 16 | 10/44/21/587 |

Spearman rank correlations across the 26 runs compare each first-draft figure with the observed
outcome. They describe this small sample; they are not an estimate of a causal effect.

| First-draft figure | Rounds | Findings | Majors | Elapsed minutes |
| --- | ---: | ---: | ---: | ---: |
| Total characters | -0.01 | 0.25 | 0.18 | 0.11 |
| Tasks | 0.24 | 0.38 | 0.47 | 0.37 |
| Largest task | 0.28 | 0.21 | 0.20 | 0.42 |
| Sum of task characters / Approach characters | 0.35 | 0.36 | 0.40 | 0.51 |
| Requirements characters | -0.13 | -0.20 | -0.21 | -0.10 |
| Approach : Requirements | 0.39 | 0.40 | 0.47 | 0.46 |
| R-items | 0.13 | 0.34 | 0.35 | 0.05 |
| Named test classes | 0.16 | -0.08 | -0.02 | 0.37 |
| `*MatrixTests` classes | -0.11 | -0.25 | -0.15 | 0.25 |

For this sample, an Approach of at least 22,000 characters marks 9 of the 15 runs with at least
7 rounds, 3 of the 5 with 6 rounds, and none of the 6 with at most 5 rounds. Six runs with at
least 7 rounds fall below the signal. The threshold sits just above the largest Approach among
the fast runs (21,681); it is a prompt to inspect separability, not a prediction of review cost.
The 190,336-character, 2-round archival plan has an 11,312-character Approach, illustrating why
the total length is a poor single rule. Change points are available in only five runs, too few
for the same comparison.

## Data and measurement

- Archival plans are `C:\Dev\pfab-plan\planreview-data\plans\<run>\round1.md`, with round
  counts, finding totals and timestamps from `C:\Dev\pfab-plan\planreview-data\rounds.jsonl`.
  The table's first 16 run IDs identify the exact plan files. The archival elapsed time is from
  the first `reviewCallTs` to the last `critiqueTs`.
- For each of the last 10 runs, the first `forge.plan.write` `planDraft` was extracted from the
  Claude transcript under
  `C:\Users\Admin\.claude\projects\C--Dev-CodexPlugins-plugins-concurrency-hunter\`.
  The run's `C:\Dev\CodexPlugins\plugins\concurrency-hunter\.forge\<run>\flow_log.md`
  supplies review rounds and finding severities, and its `forge.log` supplies first review-call
  and last review-result timestamps and the first `forge.evidence.map` `changePoints` where one
  exists. The transcript files, in table order, are:

  | Run | Transcript JSONL file |
  | --- | --- |
  | 20261006-211341-82aa0e | `8f5270aa-c6e1-42da-88b5-db6656c63e47.jsonl` |
  | 20261009-194113-1000db | `ce427139-4c09-4234-8732-208c8663d0e0.jsonl` |
  | 20261012-163837-6fc7b1 | `3e818f46-c099-45e4-81f3-0a5346e085e9.jsonl` |
  | 20261014-181155-5c1f5a | `6507b63d-4592-423f-8198-9965f08375c9.jsonl` |
  | 20261017-180043-675224 | `bffed10b-aebe-4f29-a7cb-3fd7a71e2c2f.jsonl` |
  | 20261019-215643-7586e3 | `5e9b2258-24f1-4dc6-8d3d-890727aba900.jsonl` |
  | 20261020-123405-0d2a47 | `a566f18a-5a33-47ac-989a-d65b9ebf7a38.jsonl` |
  | 20261022-105845-5a51cc | `336387ae-323d-48c5-ab5d-dae2420b74d5.jsonl` |
  | 20261022-214038-e08c4f | `a55af0ab-dd99-4e8a-8e28-2f6059c83e6c.jsonl` |
  | 20261024-105628-872ce5 | `0f41caff-a6b3-4e6e-a0e5-742fb346bfd0.jsonl` |

- Section measurements run from an exact `## Requirements` or `## Approach` heading through
  the next level-two heading. Task sizes cover each numbered Approach task through the next
  numbered task. R-items are distinct `R` plus number IDs with an optional lowercase suffix in
  Requirements. Named test classes are distinct `*Tests` identifiers anywhere in the first draft;
  they include existing classes named by the plan. B4c's 72 named classes therefore measure a
  different thing from the five **new** test classes proposed in that Run. Major totals count
  major findings, not blockers. Elapsed time is wall time and includes pauses between calls.

## Consequences and limits

- The extra figures give the user and Orchestrator a concrete chance to choose a smaller review
  unit before Critic time is spent. The choice remains the user's.
- The 22,000-character rule is calibrated on only 26 runs, chosen after observing the same data.
  It misses six runs with at least seven rounds and has no out-of-sample validation. A split may
  make one run cheaper, but this sample does not measure the effect of splitting.
- Plan subject, requirement difficulty, Critic model, review practice and human pauses vary
  across runs. Findings and rounds also depend on the Critic and revision behavior. Their rank
  associations with first-draft shape do not establish that size caused the outcomes.
- The source artifacts are local archives and user transcripts, not repository fixtures. Their
  exact locations and the derived values are recorded here, but another checkout without those
  artifacts cannot independently recompute the table.
