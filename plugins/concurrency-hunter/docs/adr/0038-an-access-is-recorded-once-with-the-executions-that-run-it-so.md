# An access is recorded once, with the executions that run it so

Status: Accepted (#458). The owner chose option A, in the collector, and the counters below.

This decision carries [ADR 0017](0017-executions-share-one-walk-graph.md)'s rule that a collected access is recorded
once with the set of executions that run it so from the walk to the accesses pairing reads. A finding, an occurrence and
a fingerprint stay what [ADR 0007](0007-a-finding-is-a-pair-of-access-sites.md) and SPEC TD-076 make them.

## Context

Access collection on OrchardCore does not reach a terminal status. Three runs of run 5g E-scale
(`docs/runs/5g-E-scale.md`) stopped in `InterproceduralAccesses.Collect`:

- **#443, code a9fdbb1d.** After 36 minutes of collection there were 2,986,763 accesses and 12.72 GB live. Copies of the
  unresolved-registration text took 5.6 GB. With those copies shared, the run stayed in collection for 3 h 40 min before
  the 58 GB commit guard stopped it, where it had stopped after 2 h 18 min before.
- **#454, code cfbe6453, a full dump after 60 minutes of collection.**
  - 8,110,072 accesses from 577 executions of 128 roots.
  - 14.77 GB live, of which the accesses alone retained 11.95 GB.
  - One execution holds up to 209,266 accesses (median 540, p90 70,281).
- **#454's change: resources, code flow steps, uncertainty lists, gap checks and deciding calls shared per scope.** Memory
  no longer stops the run: 226 minutes of collection at 12–22 GB private, stopped at the 4 h deadline. Time stops it now.
  The samples show `ExecutionCollector.Emit`, `Localities`, `CodeFlow`, `Conditions` and
  `UnknownEffectAccesses` → `DeepReadAccesses`. How many accesses a whole collection makes is unknown.

The accesses repeat. `Access` is "an access as one execution runs it on one resource", and every execution that reaches
a site emits its own copy. The #454 dump, counted by key with the execution left out:

| Key | Distinct | Accesses per key |
|---|---:|---:|
| site (body, operation, kind, span), instance, resource | 616,029 | 13.2 |
| site, resource; instance left out too | 274,762 | 29.5 |
| site, instance, resource, root, and the lengths of code flow, held locks and call path, freshness, ownership | 6,796,959 | 1.19 |

401 keys repeat more than 256 times; `FeatureAttribute.set_Description` repeats 1,096 times. Most of the multiplicity
lies across roots, not across the executions of one root.

Everything that depends on the execution sits in a few fields. The code reads them as follows (references as of 8b7eb7eb):

- **Timing needs the pair of executions, of instances and of operations on the two sides, and nothing else.**
  - The overlap policy: `ExecutionAnalysis.Decide`, `Overlaps`, `ExecutionInstance.OverlapsItself`.
  - The happens-before path, which reads `ExecutionId`, `InstanceId` and `OperationId` only.
  - The pass completion proof, already cached per execution pair and program points.
  - The same-execution rules of pairing: a hosted transient, disjoint iterations, a gap's `ExecutionFacts.Overlap`.
- **The other pair filters read what the execution does not change once the access is known.** These are the operations,
  ownership (by region), conditions (by the walk path), freshness, library-internality, the selector term and the held
  protections. The held protections are the one exception: they come from the execution's own lock fixpoint, so two
  executions can hold different locks at one site.
- **Findings group by rule, resource and the unordered pair of sites.** Occurrences are one per pair of roots and call
  paths. Inside an occurrence the kept pair is the least protected, then the smallest instance ids (`ConflictFindings.Fold`).
  Copies from two executions of one root with one call path already fall into one occurrence. No finding counts
  executions. [ADR 0042](0042-an-access-keeps-its-executions-as-a-set-and-what-executions-share-is-walked-once.md)
  makes occurrences one per pair of roots, as SPEC TD-076 says, and scores a finding over every pair that reaches it, not
  over the least protected of each pair of roots and call paths alone, which it corrects on purpose.
- **The report reads one representative access per side.** It shows its `PathRoot`, `CallPath`, `CodeFlow`, held
  protection, ownership and binding evidence.

So the execution matters to pairing in two ways only: which pairs of executions may overlap, and what locks each
execution holds. Yet today it multiplies everything else:

- **Emit's work per access.** This is resources, uncertainties, gap checks and code flow. Unknown-effect accesses are
  rebuilt in every execution, so their deep reads are walked again in every execution.
- **Memory per access.** This is the `Access` object (224 bytes on average at #443), its held protections and its
  representative state.
- **Pairing, which is all-pairs inside a bucket.** A site repeated *k* times on one resource takes *k²/2* comparisons.
  The 1,096 copies of one setter are about 600,000 comparisons, and they decide one pair of sites.

## Decision

An access is recorded once, in the collector, with each execution that makes it so (option A below).

- **The key.** Two accesses are one when they agree on everything but the execution and what the execution gives them
  besides. That is the site, instance, resource and operation, the held protections, conditions,
  construction-locality, freshness, iteration indexing, selector term, ownership, library-internality, gap checks and
  labels. Lists and maps compare by content, so a key can only fold too little.
- **An execution of the access** (`AccessExecution`). Each one keeps three things:
  - the execution's id;
  - its variant: the root, path root, call path, spawn sites, code flow and read sources that execution gives the access;
  - its position: the access's place in the order the scope's executions made their accesses.

  An access's own execution and variant are its first execution's.
- **Where.** `InterproceduralAccesses.Collect` folds each execution's accesses as the collector returns them
  (`AccessFolding`). The collection's `Folded` list holds the folded accesses. Its `Accesses` list holds each access as
  each of its executions made it, in collection order (`AccessExecutionList`), which is what the collection held before.
- **Pairing.** The index buckets folded accesses, and every folded pair is compared once.
  - The filters that read only the two accesses are decided for the pair: read-read, conflicting operation, scope,
    confinement, conditions, freshness, protection and library-internality.
  - Three questions are asked of pairs of the two accesses' executions, in the order of the filters around them: whether
    a hosted transient is one object, the timing, and the disjoint iteration.
  - The pair stands when some pair of executions passes. It gives one witness per pair of the two sides' variants: the
    first pair of executions in collection order that passes, ordered by the earlier position, then the later one; the
    witnesses of one pair of path roots and one orientation are one candidate (ADR 0042). A pair within a bucket is
    reported on its earlier execution's resource, so there each orientation gives its own candidate. A pair of one
    execution has no earlier execution: the content of its two sides gives its orientation and the resource it is
    reported on (ADR 0042).
  - Candidates are kept in that order.
  - [ADR 0042](0042-an-access-keeps-its-executions-as-a-set-and-what-executions-share-is-walked-once.md) makes these
    the witnesses of a candidate, one candidate per pair of path roots and orientation, which the solver asks once.
- **Why this keeps every finding.** A finding keeps, per occurrence, the least protected pair with the smallest instances,
  and the first of them in pair order. Within one pair of variants, every pair of executions has the same protection,
  instances, roots, call paths and code flow. So the first that passes is the pair the finding kept when every execution
  had its own copy. The solver's query reads only the conditions and selector terms, which are the pair's. Under
  ADR 0042 an occurrence is one per pair of roots, and a finding is scored over every pair this keeps, its witnesses,
  where it read only the least protected of each pair of roots and call paths before.
- **Counters.** `accesses`, comparisons, the n(n+1)/2 bound, buckets and the largest bucket count folded accesses.
  - Candidates are the candidates above.
  - A skipped pair counts once, under the reason of its first pair of executions, with that pair's assumptions. A pair
    that stands counts one comparison for each candidate it gives, so skips, suppressions and candidates still sum to
    the comparisons.
  - One counter beside them, `accessExecutions`, is the sum of the accesses' execution counts: the number of accesses
    before this decision. On the demo and eShop it equals the old `accesses`.
  - The snapshots are re-recorded in their counters only. Their findings, pairs of sites, occurrences, fingerprints and
    gaps do not move.
- **Diagnostics.** With `CH_DIAG_DIR` set, the checkpoint `accesses-end:<scope>` reports the folded accesses and the sum
  of their executions.

The collector still builds every copy before the fold drops it. Three parts of option A are left to a later change, once
the OrchardCore measurement says how much of `Emit` they hold:
- building only the key on a hit;
- the scope cache of unknown-effect accesses;
- the locality sets.

### Options considered

**A. Fold: one access per everything the pair filters read, with the set of executions that run it so.**

- **The key.** It is the site, instance, resource and operation, plus the held protections, conditions,
  construction-locality, freshness, iteration indexing, selector term, ownership and library-internality. Executions
  that agree on all of it share one access.
- **What the access carries.** Its executions as bits over the execution numbering, the set #453 already uses for a
  region's executions. Its representative per root: the `PathRoot`, `CallPath`, `SpawnSites` and `CodeFlow` of the first
  execution of that root that reached it.
- **Pairing.** Non-timing filters are decided once per pair of folded accesses. Timing is then asked per pair of
  executions from the two sets, until each pair of roots has a witness or every execution pair is ordered or cannot
  overlap. The folded access's pair with itself covers both an execution's pair with itself and the pairs between
  two of its executions, each under the rule it has today.
- **Where the fold happens.** In the collector, not after it. Emit looks up the scope-wide key first. On a hit it adds the
  execution to the set, and a representative if the root is new, and builds nothing else.
- **Execution-independent parts move to the scope.**
  - Unknown-effect accesses are cached per scope by call and holdings, so their deep reads are walked once.
  - Construction-locality can be read off ADR 0017's per-object `MayIn`/`MustIn` execution sets: it splits an
  execution set instead of being looked up per execution (`Localities`, #452).

**B. Shared body, a small header per execution.**

- **The body.** Resource, operation, symbol, source, instance, uncertainties, binding evidence, gap checks, read
  sources, selector term, freshness and ownership, built once.
- **The header, one per execution, as today's count.** Execution, root, path root, call path, spawn sites, code flow,
  held protections, conditions and construction-locality.

**C. Fold within a root only.** One access per key plus root, with the executions of that root. No pair of roots
changes, so occurrences cannot change by construction.

**D. No model change: per-scope caches inside Emit.**

- Unknown-effect accesses and their deep reads per scope. That is possible only if they become stable objects, which
  #454 found they are not.
- Code flow built only for the first execution of a root at a site.
- #452's construction key without whole-string hashing.

Rejected outright:

- **Folding instances as well** (274,762 keys). Happens-before, the pass completion proof and the solver's variable
  names answer per instance and operation, and a resource is resolved per instance. The fold would have to drop or
  re-derive the instance in every one of them, which changes how timing is proven.
- **Folding without the held protections in the key.** Two executions that hold different locks at one site would show
  one protection, and the protection result of a finding would change.

### What each option changes

| | A. Fold | B. Body + header | C. Within a root | D. Caches |
|---|---|---|---|---|
| Pairs | Fewer folded pairs, each with its witnesses; the same execution pairs survive | Unchanged | Somewhat fewer | Unchanged |
| Findings, occurrences, fingerprints | Unchanged: the same site pairs, root pairs and least protection | Unchanged | Unchanged | Unchanged |
| Gaps and coverage | Unchanged: gap checks and deciding calls are unions already; `SemanticGaps` groups by execution set | Unchanged | Unchanged | Unchanged |
| Canonical report | Unchanged if each occurrence's representative is still the first execution of its root that witnesses the pair; access, comparison and candidate counts in metrics change meaning | Unchanged; counts unchanged | Counts change a little | Unchanged |
| CONTEXT | **Access**: "carried with the execution instances that run it so"; the rule "two Accesses in two Execution instances that may overlap form a Candidate" names a pair of executions from the two sets | **Access** split into the access and its execution header | **Access**: "with the executions of one root" | None |
| SPEC | TD-075 (the index groups folded accesses; execution compatibility becomes a witness search); the metrics of section 6 (`accesses`, comparisons, the n(n+1)/2 bound) defined over folded accesses or kept as their execution count | The metrics' unit of an access | Metrics only | None |

### What each option saves, on the #454 dump (8,110,072 accesses at 60 minutes)

| | Accesses kept | Memory | Emit (CPU) | Pairing |
|---|---|---|---|---|
| A | 616,029 if the locks, conditions and locality agree across executions, more where they do not; the exact count is not measured | Access objects down by up to 13×; representatives add one entry per (access, root), at most ~6.8 million small entries pointing at already-shared lists | Per execution, only the walk, locks, conditions and the key; with the locality sets, no `Localities` lookup per execution | Comparisons over folded accesses, plus timing queries bounded by execution pairs and cached |
| B | 8,110,072 headers | Each copy keeps a header instead of a whole `Access`; roughly half the object | None | Unchanged: still *k²/2* per repeated site |
| C | At most ~6.8 million (−16%) | −16% at most | −16% at most | Somewhat fewer |
| D | 8,110,072 | None | Unknown-effect accesses and code flow no longer per execution; the rest unchanged | Unchanged |

How A, B and C bear on the work the samples show:

- **`Emit`** builds once per folded access under A, and once per header under B and C. Under D it still runs once per
  copy, with cheaper parts.
- **`Localities` (#452)** is answered per execution today. Under A, with ADR 0017's execution sets, it is answered once
  per instance, operation and region. B and C leave it per execution, and #452's string keys still matter.
- **`DeepReadAccesses`** under A and D walks once per scope per call and holdings instead of once per execution. B and C
  leave it per execution.

### Why A, in the collector

- **Only A removes the repetition both where it is built and where it is paired.**
  - B keeps 8.1 million headers and the quadratic buckets. Pairing would have to group headers by body to avoid them,
    which is A without the saving in Emit.
  - C cannot gain more than 16%, because the copies lie across roots.
  - D cuts part of Emit's time and none of the memory or pairing. Its unknown-effect cache needs A's stable accesses
    anyway.
- **A changes the representation, not what is proven.**
  - A pair of sites is a candidate exactly when some pair of copies is today. Every non-timing filter reads only key
    fields, and timing still answers each pair of executions as it does now.
  - Findings, occurrences and fingerprints are already defined without executions (TD-076, CONTEXT **Finding**).
  - ADR 0017 already records collected accesses this way. A takes the same step one stage later.
- The gates hold it: `build/check-test-baseline.ps1` with the eShop snapshot and `record-demo-models.ps1 -Check` must
  show no finding, pair of sites, occurrence or gap moving. A shift is a defect of the fold, not a consequence of this
  decision.

## Consequences

- An access no longer belongs to one execution. A reader of the folded accesses that needs the execution reads
  its `Executions`: the semantic gaps do. Every timing question is asked per pair of executions. The witness search is
  new code on the hottest path of pairing. It stops at the first pair of executions that passes, and in the worst case asks
  as many timing questions as there were pairs of copies.
- A reader that wants one access per execution reads `Accesses`, each access as each execution made it. Model
  generation and the tests read it; each view is made once, when first read.
- Held protections are part of the key. A site whose executions hold different locks stays as several accesses, which is
  where the fold saves least.
- The representative shown for an occurrence is the first pair of executions, in collection order, that witnesses the pair
  of variants. Otherwise the code flow and held protection the report shows could change without any finding changing.
  The tests pin it, and the demo's findings, compared field by field with a pairing of every copy, do not move.
- The access, comparison and candidate counts of `metrics`, `run-metadata.json`, the step counters and the demo and
  eShop snapshots count folded accesses, and `accessExecutions` keeps the old count beside them.
- An access folds only within one instance. Two executions that reach a site through different calling contexts keep
  their own accesses, which is where the fold saves least.
- An unknown effect's accesses fold where their resources agree by content. #454 keeps those resources per execution,
  so each copy is still built.
- Collection still walks every execution and builds every copy; the fold saves the memory of the copies and the pairs
  between them. Whether that is enough for OrchardCore to finish is measured after the implementation: a run under the
  lock with `CH_DIAG_DIR`, whose `accesses-end` checkpoint gives the folded accesses and their executions, and, if it stops
  again, the split of collection time between the walk, the locks and `Emit`.
