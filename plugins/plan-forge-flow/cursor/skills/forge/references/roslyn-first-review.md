# Roslyn-first reviewer contract

For every C#/.NET semantic claim, first discover the named read-only Roslyn MCP
tools. Use an absolute repository C# path and verify that the returned
document-scoped project/assembly identity belongs to the intended solution
before relying on diagnostics, definitions, references, callers,
implementations, symbol information, document symbols, or dead-code results.

If Roslyn is missing, unreachable, inconclusive, or attached to a different
solution, continue with read-only text search and the supplied diff/build/test
evidence, explicitly naming the fallback reason. That fallback is not itself a
blocker and does not by itself make coverage partial. For non-C# scope, Roslyn
is not applicable.

Every critique contains exactly one marker:

```text
ROSLYN: USED — <solution/project identity>
ROSLYN: FALLBACK — <reason>
ROSLYN: NOT_APPLICABLE
```

Roslyn supplements rather than replaces build, analyzer, test, and runtime
verification.

After ordinary `run doctor --host cursor`, perform an optional non-mutating
Roslyn capability probe with an absolute C# path. Treat missing tools,
connection failure, wrong-solution identity, and inconclusive responses only as
readiness warnings; they do not change the doctor verdict.
