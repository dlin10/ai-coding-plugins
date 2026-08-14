# OpenAI App Server lifecycle

OpenAI roles use the typed `codex app-server` JSONL stdio integration. The
client initializes once, acknowledges with `initialized`, accepts only OpenAI
API-key or ChatGPT account types, and obtains model/effort pairs only from all
pages of `model/list` and each entry's `supportedReasoningEfforts`.

On Claude, the initial `run doctor --host claude` owns executable discovery,
App Server initialization, account validation, and the catalog used for the
provider-first UI. It supports native executables plus Windows npm `.cmd` and
`.bat` shims. The orchestrator never launches Codex directly. Every actual
session repeats account and exact model/effort validation against a fresh
catalog so a stale doctor result fails closed.

Every reviewer round creates a fresh thread with `approvalPolicy: never` and a
`readOnly` sandbox. After the completed turn is read back and its OpenAI
provider, thread identity, and idle status are audited, delete the thread even
when audit processing fails. Include [openai-reviewer.md](openai-reviewer.md)
in its prompt.

Create the builder hold in a persistent `readOnly` thread. Record its thread id
without deleting it. After plan materialization, resume that exact thread with
the selected model, repository cwd, `approvalPolicy: never`, and:

```json
{
  "type": "workspaceWrite",
  "writableRoots": ["<canonical-repository-root>"],
  "networkAccess": true
}
```

`thread/start` and `thread/resume` must return `modelProvider: openai` and the
exact selected model. `thread/read` does not return a model id, so validate only
its provider, thread/session identity, and documented status there. Provider,
model, identity, and status drift fail closed.

Run long turns through the external session store:

```text
planforge session start --workspace <repo> --role reviewer|builder-hold|builder-resume \
  --model <catalog-id> --effort <advertised-effort> [--thread-id <builder-thread>]
planforge session status --workspace <repo> --session-id <id>
planforge session result --workspace <repo> --session-id <id>
planforge session cancel --workspace <repo> --session-id <id>
```

`session start` reads the prompt from stdin and launches a detached worker.
State and terminal results are atomically replaced in external plugin data;
state includes a heartbeat plus a stable error code (`auth`, `provider`,
`model`, `identity`, `protocol`, `process`, `agent`, `cancelled`, or `unknown`).
Cancellation sends `turn/interrupt`, consumes its response and terminal
`turn/completed`, then records a terminal cancelled result. Never infer
replacement eligibility from the human-readable error message.

App Server is optional for Claude workflows. Doctor absence selects the
Anthropic-only path. An installed but unusable App Server requires explicit
user consent before continuing without Codex. Automatic builder replacement is
allowed only when a thread read
confirms terminal identity loss and the stable category is
`terminal-identity-loss`. Timeout, cancellation, auth, permissions, sandbox,
model drift, protocol, process, network, and unknown failures retain the
existing hold and require explicit recovery.
