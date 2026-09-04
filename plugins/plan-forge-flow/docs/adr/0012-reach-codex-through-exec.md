# Reach Codex through `codex exec`, not the App Server

Supersedes the catalogue-source half of [0007](0007-serve-live-catalogues-to-the-interview.md); the
rest of that record stands.

Issue #58 opened as a machine fault: on a Windows 11 box where PowerShell 7 arrived from the
Microsoft Store, every command a Codex builder tried to run failed with
`CreateProcessAsUserW failed: 5`, the build came back `blocked`, and the working tree was never
touched. The fault is real and is recorded separately in
[0013](0013-strip-the-store-alias-from-the-codex-path.md). It is not the reason this record exists.
The reason is what the diagnosis exposed while looking for it: the fault sits *inside* codex, below
whichever surface we speak to, and reproducing it through `codex exec` gave a byte-identical
failure. The surface was never the cause — which made it fair to ask, for the first time since it
was written, whether the surface was the right one.

It was not, and the decisive fact is embarrassing. **Forge never holds the connection open.** All
four acts — `PlanReview`, `Build`, `CodeReview`, `ReviewFix` — take `await using var session` and
dispose it before the tool call returns, and `CodexAppServerVendor.StartAsync` starts a fresh
process every time. Every builder task therefore paid for a process launch, an `initialize` round
trip, a `thread/resume` and a turn, and then threw the connection away. The one thing an App Server
buys over a per-call CLI is the thing this codebase bought and discarded on every single call. The
MCP surface is stateless, so it could never have been otherwise: a builder's continuity across tool
calls is carried by a resume token in the run state, exactly as it is for Claude and Cursor.

With that gone, three measurements decide the rest, all against `codex` 0.147.0 on 2026-09-03:

- **Structure stops being a negotiation.** The App Server has no schema field, so codex reached
  structure the way Cursor does: schema in the prompt, validated here, retried up to
  `SchemaInPrompt.MaxAttempts`. `codex exec --output-schema` hands the schema to the Responses API
  as a strict format — the CLI refuses a schema without `additionalProperties: false` before a
  token is spent, naming `codex_output_schema` and `text.format.schema`. A prompt written to break
  it, ordering the model to ignore the output format and answer with one plain word, came back as a
  valid object carrying that word in its field. The retry loop existed because the model could
  disobey; it cannot, so the loop goes. `SchemaInPrompt` stays for Cursor, which still has no such
  channel.
- **The run log gets the process back.** `AppServerConnection.Start` called `Process.Start`
  directly, bypassing `StreamingProcess` and therefore `process.start`. A run's `forge.log` recorded
  only `git`, and codex appeared solely as `vendor.started` — which is precisely why the first
  diagnosis of #58 pointed at the MCP layer instead of at a shell. An exec vendor goes through the
  same wrapper as the other two, so this stops being a defect to fix and becomes a property of the
  shape.
- **Nothing is actually being left behind.** `codex exec resume` with an unknown id answers with a
  `thread/resume` failure and a JSON-RPC error code — an App Server method name surfacing through
  the CLI. `exec` *is* an App Server client; it is the one OpenAI maintains. Session ids are
  therefore shared, and a run begun under the old vendor resumes under the new one.

So the codex vendor is rebuilt on `codex exec`, following `ClaudeCliSession` and
`CursorAgentSession` rather than a hand-written line protocol: `--json` for the event stream,
`--output-schema` with `-o` for the result, `codex exec resume` for a builder's later tasks,
`-c developer_instructions=` for the role prompt, and `-m` with `-c model_reasoning_effort=` for the
selection. A reader loop, request correlation, a server-request decliner and a stderr ring buffer
are deleted rather than maintained.

Two details of that invocation were settled by measurement after the shape was chosen, and both
differ from the obvious reading of the CLI's help.

**The prompt travels on standard input, not as an argument.** `codex exec` takes a positional
prompt, but a code-review prompt carries a whole diff and Windows caps a command line at about
32,000 characters, so the obvious spelling has a size limit that the work will reach. Passing `-`
as the positional prompt makes codex read the whole prompt from stdin instead. Measured on
2026-09-04 for a first turn and for a resumed one, both with `--output-schema` and `-o`: exit 0, the
strict object in the result file, and the command stream intact. This supersedes the note at the
foot of this record about passing the prompt as an argument; stdin is not appended as a `<stdin>`
block when `-` is the prompt, it *is* the prompt.

**The sandbox is spelled as a configuration key, not the `-s` flag.** `codex exec resume` has no
`-s`, so a builder's first turn and its later ones would need two spellings of the same choice —
which is the exact asymmetry the App Server client was criticised for. `-c sandbox_mode="read-only"`
and `-c sandbox_mode="workspace-write"` were measured as accepted overrides on both, against an
invented key that is refused, so one spelling covers every turn. The cost is losing the `-s` flag's
pre-launch validation of a misspelt value, which is worth less than the single spelling.

A third measurement bounds what resume can change: a resumed turn kept the developer instructions
the thread started with, even when the resumed call passed different ones. The role prompt is
therefore fixed when a builder's session begins, which is all forge ever needed — it never changes
a role mid-session — but it means the instruction cannot be corrected without a new session.

Two probes move with it. The catalogue comes from `codex debug models`, which reports each model's
supported reasoning levels with descriptions, its default, its visibility and its priority. Sign-in
comes from `codex doctor --json`, whose `auth.credentials` check carries the auth mode and which
declares a `schemaVersion`.

**Rejected: staying on the App Server and fixing only the three defects.** The smallest change, and
the leading candidate until the disposal pattern turned up. Keeping it would have meant keeping a
hand-written client for a protocol whose own CLI help marks the subcommand `[experimental]`, in
order to preserve a persistent connection that is destroyed on every call.

**Rejected: a hybrid** — `exec` for the work, the App Server kept only for the probe and catalogue.
It buys the live `model/list` back at the price of maintaining both clients forever, which is the
cost the move exists to remove.

**Rejected: the official SDKs.** Neither is a third option. The TypeScript SDK spawns
`codex exec --experimental-json` per turn; the Python SDK drives `codex app-server` over stdio,
making the current C# client an unwitting reimplementation of it. There is no .NET SDK — OpenAI
publishes none — so either would put a Node or Python bridge between this server and a binary it
already launches directly.

## The costs, named

`codex debug models` is a debug command with no stability contract, and 0007 rejected it for exactly
that. That reason survives and is accepted here; what does not survive is 0007's second reason, that
the App Server path was already written, tested, and proved sign-in in the same probe. It is now the
path being deleted, so the argument is circular. Against the remaining objection stands the CLI's
own help, which marks `app-server` and both of its schema generators `[experimental]` while
documenting `codex exec` as the supported non-interactive surface. This trades one absent guarantee
for another and keeps one surface instead of two.

`codex exec` exits **0 when a command it ran was denied by the sandbox**, and 0 when that command
exited non-zero. Both were measured. A vendor that reads the exit code learns nothing about the
failure this issue is named for; status must come from the `command_execution` items in the `--json`
stream, where `status` is `failed` and `exit_code` carries the number. Only API-level failures — a
bad schema, an unknown model, an unknown effort — reach the exit code, as 1.

`--json` is chosen over the `--experimental-json` the official TypeScript SDK uses, because the
documented flag already emits everything the run log consumes: the resolved command line, the exit
code, the aggregated output and the status, as paired `item.started` and `item.completed` events.
Neither flag's event schema carries a stability guarantee.

Effort is still validated only upstream: an invalid reasoning level is accepted locally, printed in
the header, and rejected by the API. The catalogue's per-model effort list and the API's enum also
disagree, the API naming levels the catalogue omits, so the catalogue stays advisory exactly as 0007
already had it.

And `codex exec` reads stdin whether or not it is asked to. Given a positional prompt with stdin
redirected, which it always is from a server, it announces that it is reading additional input and
appends what it finds to the prompt as a `<stdin>` block — so a caller that passes the prompt as an
argument must also close stdin, or the prompt arrives twice. Passing `-` as the prompt, which is
what this vendor does, removes the trap along with the length limit.
