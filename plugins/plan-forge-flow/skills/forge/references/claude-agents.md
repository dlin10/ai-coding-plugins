# Claude agent contract

Claude Code 2.1.226 and newer uses one of six reviewer definitions and one of
six builder definitions from the plugin `agents/` directory. The suffix is the
requested effort: `none`, `low`, `medium`, `high`, `xhigh`, or `max`. `none`
omits the `effort` frontmatter field so Claude inherits its normal runtime
behavior. Every definition omits `model`; the orchestrator supplies a selected
model at invocation time, or omits it to inherit.

Reviewers have an exact allowlist: `Read`, `Grep`, `Glob`, `ToolSearch`, and the
nine named read-only Roslyn MCP tools in their frontmatter. `ToolSearch` may
only discover or load those nine tools. Reviewers have no shell, mutation,
permission, delegation, or other MCP namespace access.

Builders deliberately omit a tool allowlist. They use Claude Code's normal
coding tools and remain subject to the user's configured permissions. One
persistent builder handles one locked task or one bounded fix list per
dispatch; it never stages or commits changes.

The Claude hooks capture advisory model, effort, agent, and result evidence for
these agents. Both hooks are asynchronous and fail open. They write JSONL only
under `${CLAUDE_PLUGIN_DATA}/agent-evidence/`, never inside the repository or
the versioned plugin directory. Follow the exact alias, effort, normalization,
and swap-validation contract in [claude-model-selection.md](claude-model-selection.md).
