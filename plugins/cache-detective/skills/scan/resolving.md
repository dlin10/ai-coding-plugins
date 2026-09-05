# Resolving unresolved analysis

Use annotations only where source or configuration establishes the missing fact. Do not edit
application files; an annotation records the conclusion separately.

## `key`

Find the cache invocation and its store, TTL, tags, and semantic. Use `{ "template": "..." }`.
For a key without a literal segment, annotate the template as written when its shape is still known;
for example, the eShop basket key can become a `store`. Do not guess a template from unavailable code.

## `sql`

Inspect SQL text, EF mapping, or naming conventions. Use `{ "reads": ["schema.Table"],
"writes": ["schema.Table"], "procs": ["schema.Procedure"] }` with at least one nonempty array.
Do not annotate dynamic SQL whose selected object is not statically constrained.

## `call`

Use `{ "target": "handler:<Solution>/<Symbol>" }` for a proven code target or endpoint; use
`{ "external": true }` for a confirmed repository boundary. With several endpoints, choose only after
checking base address from `AddHttpClient` or configuration; never choose by route spelling alone.

## `event`

For an event gap use `{ "handlers": ["handler:<Solution>/<Symbol>"] }`; for an unknown event type at
a marked call site use `{ "events": ["Full.Event.Type"] }`. Use `{ "external": true }` only when a
consumer is known to be outside the repository. Bus registration does not establish a consumer.

## `role`

Use `{ "role": "cache" }` or `{ "role": "store", "store": "..." }` after inspecting actual use.

## `cache_api`

Use `{ "type": "Full.Type", "store": "name", "methods": [...] }`, with every method's semantic
and key argument. Add it only when the relevant API surface is known.

## `event_api`

Use the configured event-recognizer form (`publisher`, `consumer`, optional methods and argument
positions). Confirm the publisher and consumer interface shape; do not infer a bus from `Publish` alone.
