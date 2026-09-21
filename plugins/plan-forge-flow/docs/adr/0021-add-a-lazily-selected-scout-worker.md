# Add a lazily selected Scout Worker for bounded reconnaissance

## Context

The Orchestrator sometimes needs broad repository reconnaissance before it can settle requirements
or write a plan. Keeping multi-module exploration, caller and writer discovery, dependency tracing,
or greenfield orientation in the host conversation materially expands its context. The Critic and
Builder cannot absorb that work: the Critic judges independently, while the Builder edits against
an approved plan.

The existing Vendor seam already provides read-only Worker launches, structured output, resumable
sessions, live model catalogues, Worker-tool grants, sensitive-input checks, and Run-scoped state.
A reconnaissance role can reuse those facilities without turning evidence into another source of
requirements or instructions.

## Decision

Add **Scout** as a fourth Worker role. It receives one bounded research question and the minimum
Run context needed to answer it, runs under the selected Vendor's existing read-only boundary, and
uses only the Run's granted read-only Worker tools. It returns sourced evidence and never plans,
judges, edits, or settles requirements. The Orchestrator remains responsible for deciding what the
evidence changes.

Scout selection is lazy and independent of the Critic and Builder selections. On the first need,
the Orchestrator reads the live catalogues and asks for one valid Vendor, model, and effort
combination, also offering to continue without Scout. The exact choice is retained for the Run and
reused without another question. Choosing to continue without Scout suppresses later automatic
questions for that Run, although an explicit later choice may enable it. A failed Scout never
silently falls back; retry keeps the selection, while explicit reselection starts a fresh Scout
session and is recorded.

Calls resume one Scout session when the Vendor supports it, so repository orientation can remain
outside the Orchestrator's context. Each call answers only its current bounded question. Its full
structured report atomically replaces the Run's Scout report document, while the tool result carries
only a bounded digest and document metadata. The Run log preserves call history; the report does not
accumulate unrelated earlier answers.

Scout evidence is not automatically copied into Critic or Builder prompts. Both the question sent
to the Vendor and the report written to disk pass the applicable sensitive-content checks, and the
report stores neither raw secrets nor unrestricted command output. Scout may always use internet
access that its Vendor or the Run's granted read-only Worker tools already provide; there is no
second per-call permission. Repository evidence cites a path plus line or symbol, while external
evidence cites its URL. This decision adds no connector and widens no Worker-tool grant.

## Consequences

Broad discovery can move to a persistent read-only Worker without changing who owns requirements,
planning, approval, or review. One or two targeted semantic lookups remain cheaper and clearer in
the Orchestrator.

The Run state and status surface gain a third independent Worker selection and session identity.
The MCP surface, background-Job dispatch, source-generated JSON contracts, prompts, packaging
assertions, and orchestration skill must all recognize Scout. Each Vendor must prove that Scout uses
the same read-only launch boundary as a Critic.

The latest report is intentionally a snapshot, not a reconnaissance archive. Earlier conclusions
can be recovered from the Run log or re-asked through the resumed session, but they are not silently
presented as current evidence. A Scout failure leaves the prior report and the chosen selection
explainable and requires the Orchestrator to retry, explicitly reselect, or continue without Scout.
