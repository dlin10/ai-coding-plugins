# Diagnostics stop at a time budget and name what they skipped

`roslyn_get_diagnostics` can be asked about the **affected projects** or the whole solution, and on a
large solution computing every compilation can take longer than a host waits. Cursor cancels an MCP
call at a hard 60 seconds that nothing configures (measured 2026-08-18, recorded in
`plugins/plan-forge-flow/CONTEXT.md`), and the Codex configuration this plugin writes sets no
`tool_timeout_sec`. A cancelled call returns nothing, which is worse than returning part. So the tool
checks projects in dependency order under a budget of about 45 seconds and returns what it checked,
with every **unchecked project** named and the response marked incomplete.

## Considered Options

- **No budget** is correct on small solutions and returns nothing at all on the ones where the tool
  matters most.
- **A cap on the number of projects** is predictable but does not bound time: one heavy project can
  outlast the host on its own.

## Consequences

- No errors in a response means no errors in the checked projects only. The unchecked ones are named
  so that silence about them is never read as a clean result, and a client narrows the scope and asks
  again.
- Dependency order checks the projects holding the changed files before the projects that depend on
  them, so a budget that runs out leaves the edited code checked and the most distant consumers not.
- The same rule governs `roslyn_describe_type`'s scan of referenced assemblies for extension methods:
  the declared members always return, and a scan cut short names the assemblies it did not reach.
