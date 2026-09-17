# Detect a killed background task rather than forbid backgrounding

A worker runs `claude -p`, which ends at the model's final message and kills whatever the model left
running in the background about five seconds later. Task 8 of run `20260916-134641-21e3d5` started a
ten-minute grid that way, said it would continue when the grid finished, and exited 0; the gate
found the grid's output missing minutes later, and the orchestrator ran the script by hand (issue
#91). A claude session now notes every backgrounded task still running when the final result
arrives, and a builder turn that left one comes back as **`background_killed`**, naming the
command: the server runs no gate for it, the task stays the next one, and the next attempt is told
what was killed and why.

**Why not forbid it, when forbidding works.** `--disallowedTools "Bash(run_in_background:true)"` was
measured on 2026-09-17 and refuses the call cleanly. It was rejected for two reasons:

- **Backgrounding inside a turn is legitimate.** A builder can start the suite in the background,
  keep editing, and collect the result through `TaskOutput` or `Monitor` before it answers. The
  failure is ending the turn with the task still running, not starting it — and only detection can
  tell the two apart.
- **The harness pushes the other way.** Claude Code refuses a foreground `sleep` and tells the model
  to use `run_in_background` instead. A deny rule would meet a model with two refusals that
  contradict each other, and what it does next is not predictable.

So the prevention lives in the builder and critic contracts, which state that the session ends at
the final message, and the server catches what the contract did not. `BASH_MAX_TIMEOUT_MS` is raised
to 30 minutes for every claude worker so that a long command has a foreground way to run: the gate
the host runs after the turn takes up to 20, and the Claude host allows an hour for the call that
holds both. The 30-minute idle reaper does not interfere, because a foreground call emits a
`tool_progress` heartbeat every 30 seconds.

**Why no gate.** The builder's own account is that the work was still in progress. Running a gate
against it spends minutes to report what the stream already said, which is the cost the issue was
about. The price is a retry when the task left something running on purpose, such as a server its
test no longer needed; the retry prompt says what was killed.

**Scope.** Claude only: it is the one vendor whose stream was measured to report the kill. A critic
that leaves a task behind is logged and noted in the flow log, and its critique stands — the
orchestrator filters every critique anyway, and discarding the round would throw its findings away.
