# Sanitized evidence fixture

Builder: codex / fixture-model / high

## Requirements

1. **R1.** All downstream readers handle newly supported records without losing work.
2. **R2.** Existing ordering and wire assignments remain stable and verification covers each changed contract.

**Out of scope.** Reviving LegacyBridge and its retired compatibility mode.

## Gates

1. **G1.** `dotnet test --filter Category!=Integration` passes. (R1, R2)

## Approach

1. **Extend the producer.** Teach UnknownCalls.Of to emit SummarySpawn alongside existing records. **Gate:** producer tests pass. (R1)
2. **Update all consumers.** Every consumer of UnknownCalls.Of, including ConsumerGraph and SpawnCollector, must handle SummarySpawn without discarding work. Test both consumers and their callers. **Gate:** consumer tests pass. (R1)
3. **Preserve ordering.** Keep OrderPolicy depth first; verify stable visitation order and repeatability. **Gate:** ordering tests pass. (R2)
4. **Preserve wire assignments.** Keep WireCache assignments stable, test round trips and compatibility with stored records. **Gate:** wire tests pass. (R2)
5. **Record display context.** PlatformBanner describes local display preferences only. **Gate:** context is recorded. (R2)
6. **Decide retry limits.** RetryBudget needs investigation; the available evidence does not establish its bound or failure policy. **Gate:** document what is still unknown. (R2)
7. **List available probes.** ProbeCatalog is mentioned for orientation; this task commits to no probe behaviour changes or verification. **Gate:** catalogue list is readable. (R2)
8. **Inspect compatibility.** ContractProbe needs a compatibility check, but its boundary is not documented. **Gate:** record the compatibility result. (R2)
