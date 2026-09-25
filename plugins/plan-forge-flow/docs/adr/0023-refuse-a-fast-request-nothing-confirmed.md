# Refuse a Fast request nothing has confirmed, and ask for standard speed out loud

Every vendor sells a Fast tier for some models at a higher usage price (issue #116), and each one
spells it differently: `-c service_tier="fast"` for codex, `"fastMode": true` in claude's
`--settings`, a `-fast` suffix inside a cursor model id. So `Selection` gains a third axis, `Fast`,
kept apart from model and effort for the reason effort already is. The join belongs in the vendor.

What makes this more than a new argument is that two of the three vendors fail silently, in
opposite directions. Codex does not validate the tier, so a request it cannot honour runs at
standard speed without a word. And a codex worker *without* a request inherits `service_tier`
from `~/.codex/config.toml`, which the Codex desktop app rewrites whenever its Fast toggle changes.
A run that never asked for Fast could therefore be billed for it. Claude falls back to standard
speed on its own when fast is unavailable or rate-limited. `CONTEXT.md` has the measurements.

**So a Fast request is refused unless something has confirmed it.** The Catalogue confirms it for
every vendor:

- **codex**: the model's `service_tiers` in `codex debug models`.
- **cursor**: an observed `-fast` id for the chosen effort.
- **claude**: the probe's `--bare` resolve with the opt-in, whose `init` judges the model alone,
  and one `init` with the account visible, which names an account-level refusal such as
  `extra_usage_disabled`.

Claude's own session start confirms it again: `init` reports `fast_mode_state` before any API
call, so an attempt whose request will not be served is killed there and fails with the reason. A
model the Catalogue does not list cannot be confirmed, so Fast on it is refused too.

This is a deliberate exception to [0007](0007-serve-live-catalogues-to-the-interview.md), which
keeps the Catalogue advisory. Model and effort stay advisory, because a wrong one fails loudly: in
seconds for cursor, at the API for claude. An unconfirmed Fast request is the one that fails
quietly and costs money either way.

**Off is sent, not implied.** With Fast off, a codex worker is given `service_tier="default"`, so
the desktop app's toggle no longer reaches forge. **A `-fast` already written into a cursor model
or effort is read as a Fast request** rather than passed through as a raw id. Otherwise the
Requested selection would say off while a fast id ran.

**What was served is recorded only where the vendor says it.** Claude reports `fast_mode_state`
again on `result`. A `cooldown` there means the turn fell back part-way, and that is a warning on
a result that still counts. Codex reports the served tier only in its diagnostic log (`RUST_LOG`,
on stderr), which has no stability contract, so codex's guarantee is the check before launch and
the explicit off rather than a reading after it.

**Considered and rejected**

- *Pass a claude request through and warn afterwards*, as the issue first proposed. `init` answers
  before any API call, so refusing costs nothing, and a warning after the turn cannot stop it
  running at a speed nobody chose.
- *Inherit the codex configuration when Fast is off*, and *a third, "unset" state* that inherits
  it. Both leave the desktop toggle able to change what a run costs without appearing in the
  run. The third state also adds a value to every tool and to the interview.
- *Read codex's served tier out of its stderr.* It is accurate today, but it ties the server to
  a log format and makes every worker's stderr much larger.
- *Pass a cursor `-fast` id through unchanged*, or *refuse it*. Passing it through makes
  telemetry wrong. Refusing it breaks a selection stored before this change, such as a Scout
  stored with effort `high-fast`.

**Consequences.** A Fast request needs a successful probe of its vendor. The first such act after
a server restart waits for that probe, up to the cache's 60-second cap, and a probe that fails
refuses Fast rather than guessing. The Catalogue is a snapshot for the life of the server process.
An account change made mid-session, such as claude extra usage switched on, reaches the Catalogue
only after a restart. A change the other way is still caught by claude's own `init`. Codex offers
Fast on every listed model today, so for codex the check guards against a future catalogue rather
than today's.
