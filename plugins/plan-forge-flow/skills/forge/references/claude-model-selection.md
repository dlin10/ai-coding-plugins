# Claude model selection

Claude reviewer and builder invocations accept exactly five requested aliases:
`sonnet`, `opus`, `haiku`, `fable`, and `inherit`. Keep the requested alias in
the run evidence. For `inherit`, omit the Agent tool's `model` argument; for
the other four aliases, pass that alias unchanged.

The allowed effort matrix is exact:

| Alias | Allowed effort |
| --- | --- |
| `haiku` | `none` |
| `sonnet`, `opus`, `fable` | `low`, `medium`, `high`, `xhigh`, `max` |
| `inherit` | `none`, `low`, `medium`, `high`, `xhigh`, `max` |

Use the matching effort-specific agent definition. `none` means the definition
omits its `effort` frontmatter; it remains the requested effort in evidence.
Reject any other alias/effort pair before dispatch.

Completed Agent evidence must include a non-empty `resolvedModel`. If
`modelsUsed` is absent, normalize it to `[resolvedModel]`. For a family alias,
the resolved model and every model used must remain in that requested family.
For `inherit`, every model used must exactly equal the resolved model. Reject
missing resolution data and model swaps; never silently rewrite the requested
alias.

Before planning, `run doctor` rejects non-empty
`CLAUDE_CODE_EFFORT_LEVEL` or `CLAUDE_CODE_SUBAGENT_MODEL` in a detected Claude
Code shell because either variable can override Forge's approved selection.
Unset the named variables and rerun doctor. Other hosts are unaffected by
Claude-only environment configuration.
