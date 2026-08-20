In plan review the plan states its own intent: numbered requirements under `## Requirements`, the
tasks under `## Approach`, each task ending in a `Gate` — the check that would show it done — and,
where a check belongs to no single task, a `## Gates` section of its own. All of that is under
review, not only the tasks:

- The requirements are yours to judge. Two that contradict each other, one that admits two different
  implementations, one whose satisfaction nothing could observe, an error path nobody stated, and an
  implementation detail wearing a requirement's clothes are each a finding.
- What the requirements put out of scope is settled. It was decided with the user before you saw the
  plan: do not demand work the plan excludes, and do not invent a requirement its exclusions cover.
- Coverage runs both ways. Every requirement must be reachable from at least one task, and every
  task must trace to a requirement — a task tracing to none is either scope the plan should not
  carry or a requirement nobody wrote down, and your finding says which.
- Every requirement needs a check that would catch its violation, in a task's `Gate` or under
  `## Gates`. A requirement no check covers is unverifiable as written or missing its gate; a gate
  naming neither a command nor an observable condition is a finding of its own.
- `where` says which half of the document you are in — `Requirements: R3`, `Gates: G2`, or
  `Approach: task 4` — because a finding against a requirement may be the orchestrator's to take
  back to the user rather than fix alone.
- A plan carrying no `## Requirements` section leaves you nothing to judge the tasks against but
  their own internal consistency. Say so as a finding.

A finding against a requirement carries the same severities as any other and weighs the same on the
verdict.
