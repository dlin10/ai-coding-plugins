# Resolve claude's model aliases through the CLI, and discover its families through the model

Retires the `declarative` catalogue that `docs/adr/0007` left in place for claude and named as its
own cost: "still a list written down in this repo". The list itself was not the problem — `--model`
takes an alias, and `opus` or `sonnet` resolves to the newest model of that family on its own. The
problem was that nobody could tell, at the model question, what an alias stood for: whether `fable`
meant 5.1 or 5, and whether a family the repo had never heard of existed at all. The catalogue
becomes **resolved**: the aliases are still remembered here, but the probe turns each one into the
concrete model id the CLI would send, and an alias the CLI does not resolve is not offered.

The obvious source — asking the model, since Claude Code's own system prompt carries a block listing
the current models and their ids — was measured and rejected as a source of **ids**. On 2026-09-02
that block said `claude-fable-5-1` while `claude -p --model fable` reported `claude-fable-5` in its
`init` event; haiku's copy of the block said `claude-fable-5` again. The block is prose that
Anthropic edits per release and per model, and what the alias resolves to is a table inside the
CLI. Only the second is what the run would actually use. So ids come from the `init` event of
`claude -p --bare --no-session-persistence --model <alias>`, killed as soon as that line arrives:
`--bare` skips hooks, MCP servers and the keychain, `init` is emitted before any API call, and the
alias table is local — the probe is free, offline, and about four seconds for the whole list in
parallel. An unknown alias echoes itself in `init` (measured: `"model":"nosuchmodel"`) and only fails
forty seconds later, which is why "resolved" means `model != alias`, not "init arrived", and why
each alias gets its own twenty-second bound inside the cache's sixty.

The same block is kept as a source of **family names**, because that is the one thing the CLI has
no other way to tell us. One further process, without `--bare` and without `--model`, asks for the
families under a schema; only `family` is read, regex-checked before it can reach a `--model`
argument, and unioned with the four the repo remembers; ids the model offered are discarded. This
is the probe's one billed turn, and it does three jobs at once: it proves sign-in — its `init`
before auth, its result after — so a `Not logged in` makes claude unavailable exactly as codex's
probe does; its own `init`, having been started without `--model`, names the CLI's default, which
becomes `isDefault`; and any family it names beyond the four is resolved in a second wave. A
discovery that fails for any reason other than sign-in — a timeout, an empty answer, the block
gone from a future release — leaves the four remembered aliases and says so in the probe's detail.

Ordering follows the resolved ids, version-sorted the way `CursorAgentVendor.VersionSegments`
already sorts cursor's families, so the skill's "newest first" is finally true for claude; ties
keep the remembered order, `fable` before `opus`. The `Live` flag on `VendorCatalog` becomes a
two-valued source, `live` or `resolved`, and `forge.models` and the skill say which; nothing is
`declarative` any more, so the word leaves `CONTEXT.md` and the skill together.

Rejected: recording the resolved id lazily from the critic's or builder's own `init` when it starts
— free and always true, but a second too late for the question it answers. Probing without
`--bare` — ten seconds instead of four, and every probe process brought up the user's whole MCP and
hook set as a side effect nobody asked for. Keeping an unresolved alias in the list with a marker —
the false confidence this decision exists to remove. Sorting discovered families to the head or
the tail by fiat rather than by version. And a mock seam around `StreamingProcess` for tests: the
parsing and merging are pure functions on lines, tested on fixtures the way cursor's are, and the
early stop after `init` uses the enumerator's existing abandon path, so the run log carries one
`warn` per alias with reason `abandoned` — accepted, and preceded by an `info` line from the probe
saying the stop was the point.
