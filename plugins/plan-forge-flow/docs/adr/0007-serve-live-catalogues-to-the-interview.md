# Serve the vendors' live model catalogues to the interview

Reverses a standing line of `skills/forge/SKILL.md`: "The server publishes no catalogue, and
`forge.begin` returns none, so the combinations come from the orchestrator's own knowledge of that
vendor's current line-up." That knowledge is stale by construction — a run offered `gpt-5.6-sol`
with the caveat that the orchestrator had never heard of it, while `cursor-agent --list-models`
listed it across multiple effort levels. Meanwhile the code already fetched the answer and threw it
away: `ProbeAsync` filled `IVendor.Catalog` for codex and cursor, had no production caller, and no
tool read the result (issue #23).

So the catalogues now reach the interview. `forge.begin` starts every vendor's probe in the
background — fire and forget, so its own reply does not wait — and a new `forge.models` tool serves
the results: per vendor, its availability with the probe's reason, whether the list is `live` (the
vendor reported it) or `declarative` (claude, whose CLI publishes no list; its five effort levels
were re-verified against `claude --help` on 2026-08-19), and the models with display names, effort
levels and the vendor's own defaults. The skill's model question offers what came back, newest
first, and a vendor whose probe failed is not offered at all. A successful probe is cached for the
server process; a failed one is not, because its cause — a missing binary, a sign-in — is fixable
mid-session and the next call should see the fix.

Two vendor-specific choices are grounded in measurement rather than the CLI's own documentation:

- **Codex keeps its wire order.** `model/list` already arrives curated newest-first and marks
  `isDefault`, so sorting it by parsed version would only destroy information (sol/terra/luna share
  a version and are deliberately ordered). Cursor's `--list-models` order is *not* recency, so its
  families are sorted by the version parsed out of the id — segment-wise and numeric, because
  `claude-opus-4-8` is the two-segment 4.8, not 48 — with versionless ids at the tail.
- **Cursor's ~200 raw ids collapse into families, and the existing suffix join stays.** The bracket
  overrides the CLI's own tip advertises (`model[effort=high,fast=false]`) were measured on
  2026-08-19 and rejected — including the tip's own example — so families advertise exactly the
  variants the list contained, and the join can only rebuild ids that were observed. The `default`
  variant names a family's bare id and joins to nothing.

Rejected: `codex debug models` as the catalogue source — cheaper (no app-server session, no
sign-in round trip) and it carries `priority`/`visibility`, but it is a debug command with no
stability contract, and the app-server path was already implemented, tested, and proves sign-in in
the same probe (measured at ~1.4 s to the first page, cheap enough for the background). Folding the
catalogues into `forge.begin`'s reply — every run would pay for vendors it will not use, and the
reply would wait on the slowest probe. And validating the user's free-text model against the
catalogue: it stays advisory, the vendor CLI decides, and an unknown model remains a warning.

The costs, named. The interview depends on a background task the user never sees; a probe that
hangs is bounded by a 60-second cap in the cache, not by the host's clock. The catalogue is a
process-lifetime snapshot, so a model released mid-session appears only after a server restart —
the same staleness window as the CLI binary itself. And claude's declarative list is still a list
written down in this repo, now labelled as such rather than pretended live.
