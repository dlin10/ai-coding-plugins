# The recording of committed generated models has a run limit of its own

TD-034c gives a run two server constants for generation: 900 s inside generations for the whole run, and
120 s for each member. A run writes only final answers. When the run limit cuts it, two kinds of work are
lost, and the next run repeats them:

- the member in flight, which is answered `budget`;
- every answer whose dependency chain is not final yet.

For a run of the hunt, that is the right trade: the run stays bounded, and what it could not generate
stays opaque until a later run.

The recording of a repository's committed generated models (`build/record-demo-models.ps1` for the demo)
is a different run. It must answer every target, because a `budget` answer is never stored. After any
change of the generator version it generates everything again. Under the run limit, the recording
therefore repeated itself until no target was cut:

- **Six runs.** On the demo, the six runs spent 5030 s inside generations and 88 min in all.
- **One run without the limit.** One run spent 3072 s and 52 min, and wrote byte-identical files.
- **Where the 1959 s went.** Of the difference, 255 s was members in flight at a cut. 1710 s was non-final
  answers generated again, net of the round repeats that one run pays anyway.
- **Why passes cannot replace the budget.** The chain that was cut, the hosts' builders down to routing and
  Kestrel, needs 965–1010 s inside one run. That is more than the run limit, so no capped run can
  finalize it. The series converged only because each cut run still wrote a few final leaves and its new
  timeouts. A cut run that writes nothing would repeat forever.
- **Speedups never reached the recording.** The members a faster engine brought under the member limit
  lie in that chain, so they were generated in every cut run.

## The decision

The run that records committed generated models has a run limit of its own, the **recording limit**: two
hours. Every other run keeps the run limit.

- **The constant.** The recording limit is a server constant beside the run limit (`RECORDING_LIMIT` in
  `RunGeneration`), not a number a caller chooses. It is sized to exceed what one recording needs, so that
  the recording is one run: the demo needs about 3100 s, so two hours leave more than twice that. It still
  ends a recording that does not converge.
- **Who selects it.** The analyzer selects it only when its caller asks for it, by a parameter of the
  analysis (`GenerationBudget.Recording`) that, like the mode, no file or environment variable sets.
  `metrics` passes it for `--recording`, which only the recording script uses, and only with
  `--generation on`. The MCP run's seam to the analyzer has no such parameter, so a hunt never runs under
  the recording limit.
- **What stays the same.** The member limit, the order of targets, the rounds and finality are unchanged.
  A recording whose limit never binds writes only outcomes the member limit decides, as the capped series
  eventually did.
- **The guard stays.** The recording script still refuses a scope target left at `budget`. If the
  recording limit ever binds, the script fails loudly instead of repeating.

## Consequences to hold

- **Growth must be watched.** A recording that grows past two hours on a slow machine fails rather than
  loops. When that happens, the constant is raised by a decision, not by a retry.
- **More memory in one process.** One long run holds the whole recording in one process: 13.1 GB peak on
  the demo, against at most 12.2 GB per capped run.

## Rejected

- **Repeated passes as the rule.** No change to the run limit; the script reruns itself until no target is
  cut. Measured: six passes, not two, 64 % more generation, and convergence that depends on every cut run
  still writing something.
- **A run limit the caller sets as a number.** That would be a second budget anyone could tune, while the
  run limit is a server constant.
- **Storing non-final answers so that a cut run resumes.** That contradicts the finality of what a file
  holds, and needs a resumable round state for no gain over one uncut run.
