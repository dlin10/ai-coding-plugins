# The skill drives the run; the server holds its state and its deadline

The SPEC draft drew a "plugin command adapter" that starts the analyzer, waits for its terminal
state, and on the 30-minute deadline cancels every AI session and subagent the run created. On
Codex, Claude Code and Cursor a plugin command is a skill, the only programmatic surface is a
stdio MCP server, and none of the three hosts exposes MCP sampling or subagent cancellation to a
server. The adapter cannot be built.

The skill orchestrates the run, exactly as `cache-detective`'s `scan` skill does. The server is a
state machine reached through versioned tools: it starts the deterministic analysis as a job the
skill polls (the shape `plan-forge-flow` uses for long work behind a 10-minute tool timeout), hands
out bounded semantic-gap packets, validates and applies what the skill brings back, and renders
the report bundle. The server owns the deadline: after it, no packet is handed out and every result
that arrives is recorded as late and never applied. The skill is instructed not to start AI work
once the server reports the deadline exceeded.

The consequence is that cancelling a subagent already running is best-effort and the SPEC says so.
The guarantee that survives is the one that matters for correctness: a late response never changes
findings, coverage or cache, and the terminal status names the reason. The "versioned plugin-adapter
protocol" becomes the tool contract plus `SKILL.md`; a single finite analyzer process per run and
no supported headless entry point both stay.

The guarantee, stated precisely: facts enter the server only through its validating submit path,
and the deadline is the server's, whoever runs the model. That is why the tool handlers behind
`submit_inferences` and `submit_narrative` are thin wrappers over an internal service from the first
phase, not the place where validation lives.

Rejected: server-driven orchestration through MCP sampling, because no target host implements it.
Rejected: the server calling a third-party AI provider of its own, because FR-11 requires the AI to
be the host's own vendor. Deferred, not rejected: the server launching that vendor's CLI itself, as
`plan-forge-flow` reaches Codex through `exec` (its ADR 0012). It would make cancellation real by
killing a process and let the resolver run inside the job without an interlude, at the price of a
second consumer of the user's subscription outside the host session. It is admissible only through
the same submit path and the same deadline; the host-session mode stays the default because it
needs nothing installed.
