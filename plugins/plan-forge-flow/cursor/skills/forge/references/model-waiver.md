# Cursor model waiver

Cursor 3.15.6 does not expose reliable evidence that a requested subagent model and effort override was honored. Model selection remains free text, but every run requires explicit user consent before reviewer dispatch and again for the builder selection.

Record all of the following in PendingRun:

- `modelGuarantee: waived`
- the actual Cursor version observed for this run (do not substitute a documented or assumed version)
- role (`reviewer` or `builder`)
- requested model and effort as separate values
- observed selector state (`Auto` or `unavailable`)
- the user's bounded reason/consent text
- timestamp

Never silently run a reviewer with inherited settings and never label the result as a strong model guarantee. The recommended reviewer is `gpt-5.6-sol/xhigh`; the recommended implementation builder is `gpt-5.6-terra/medium`. Accept other free-text choices only after the same explicit waiver.
