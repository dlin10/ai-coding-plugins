# Roslyn-first review

Applies to every critic, whatever the vendor. One file, because the contract does not differ by
vendor — the two per-host copies it replaces had drifted into saying subtly different things.

For a claim about C#/.NET semantics — a symbol's references, callers, definition, implementations,
document symbols, or whether something is dead — use the read-only Roslyn MCP tools before text
search. Discover them first if they are not already exposed.

Before relying on a Roslyn answer, verify identity: use an absolute path to a C# file inside the
repository and check that the project or assembly it reports back belongs to the solution under
review. A Roslyn server attached to a different workspace answers confidently and wrongly.

When the tools are absent, unreachable, or attached to another solution, fall back to reading and
searching the supplied diff, build output, and tests — and say which of those it was. The fallback
is not itself a finding, and does not by itself make a review incomplete. For a repository with no
C#, Roslyn does not apply.

Roslyn is fast evidence, not proof. It supplements the build, the analyzers, the tests, and actually
running the thing; it replaces none of them.
