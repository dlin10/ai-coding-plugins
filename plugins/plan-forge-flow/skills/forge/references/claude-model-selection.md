# Claude model selection

## Provider readiness and selection

Use only the `codex` object returned by the initial `planforge run doctor
--host claude --workspace <repo>` call. Never launch Codex or query a catalog
directly from the orchestrator.

- `absent`: do not mention an unavailable OpenAI option; select the reviewer
  and builder from the Anthropic branch.
- `unusable`: show `executable`, `launchKind`, `errorCode`, and `error`, then ask
  exactly whether to continue without Codex or stop and repair it. Continuing
  disables OpenAI for both roles for the rest of the run. Stopping ends before
  Act 1 without artifacts or a consumed selection attempt.
- `ready`: ask reviewer and builder independently whether to use Anthropic or
  OpenAI. For each role ask model first and effort second.

For Anthropic, show `opus`, `sonnet`, `haiku`, and `fable`; accept `inherit`
through Other. For OpenAI, show the first four catalog models in returned order
and accept another exact catalog ID through Other. Offer only efforts valid for
the chosen model after removing forbidden `ultra`; choose a sole remaining
effort without another question. When more than four values remain, show the
first four and accept another exact value through Other. Manual OpenAI values
must exist in the doctor catalog. A later App Server session revalidates the
exact pair against a fresh catalog.

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
