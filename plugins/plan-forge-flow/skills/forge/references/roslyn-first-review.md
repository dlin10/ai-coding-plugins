# Roslyn-first reviewer contract

Apply this contract to every Claude, Codex, Cursor, and OpenAI App Server plan,
code, or fix reviewer.

For a C#/.NET semantic claim, discover or load the named read-only Roslyn MCP
tools first. Choose an absolute C# file path inside the repository and make a
Roslyn request that returns document-scoped compilation identity. Compare the
returned absolute path and project/assembly identity with the repository's
intended solution or project before relying on Roslyn. Only then use Roslyn for
diagnostics, definitions, references, callers, implementations, symbol
information, document symbols, or dead-code conclusions.

If Roslyn is missing, unreachable, inconclusive, or attached to a different
solution, continue with `Read`, `Grep`, `Glob`, and the supplied diff/build/test
evidence. State that exact fallback reason. A fallback is not itself a blocker
and does not by itself make coverage `PARTIAL`. For a repository with no C# or
.NET scope, mark Roslyn not applicable.

Every critique must contain exactly one of these audit-marker lines:

```text
ROSLYN: USED — <solution/project identity>
ROSLYN: FALLBACK — <reason>
ROSLYN: NOT_APPLICABLE
```

Do not emit more than one marker. Roslyn validation is fast semantic evidence;
it supplements rather than replaces the required build, analyzers, tests, and
runtime verification.

## Optional post-doctor capability probe

After the ordinary `run doctor` command, inspect its structured `roslyn` field.
This probe never changes the doctor verdict. When C#/.NET is not
applicable, report `ROSLYN: NOT_APPLICABLE` readiness and continue. Otherwise:

1. Use tool discovery to locate the named Roslyn MCP tools.
2. Call a read-only Roslyn tool with an absolute repository C# path.
3. Check the returned path and compilation project/assembly identity against
   the intended solution.
4. Report the capability as ready only after that check. Missing tools,
   connection failure, wrong-solution identity, or an inconclusive response is
   a nonblocking readiness warning and activates the audited text fallback.

The CLI's `configured` status means only that a repository-local
`.roslynmcp.json` was valid. It never proves server capability or solution
identity.
