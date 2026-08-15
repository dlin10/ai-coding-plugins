# Ask for approval through the orchestrator, not through elicitation

Supersedes the approval half of [0002](0002-mcp-server-surface-without-enforcement.md); the rest of
that decision — no enforcement, no canvas, no task extension — stands.

`forge.plan.approve` and every line of elicitation machinery are deleted. `forge.plan.confirm` is the
only approval route: the orchestrator shows the user the plan, asks, and passes back the answer. The
working-tree drift moves from the approval call to `forge.status`, because the orchestrator has to
show drift to the user *before* asking, and on the decision call it would arrive too late to affect
the decision it exists to inform. The surface returns to six tools, and `IOrchestrator`,
`NegotiatedOrchestrator`, `PlanPresentation` and `CanElicitApproval` go with it.

The reason is a measurement, recorded in `CONTEXT.md`: a host can declare the elicitation capability,
answer the request on the user's behalf, and render nothing. That reply is indistinguishable at the
server from the user refusing — no field separates them, and adding one would not help, since a host
answering for the user can spell its answer however it likes. The failure is silent on both sides:
the user sees no dialog and the orchestrator sees an ordinary refusal, so a stalled run explains
itself to nobody. This was not hypothetical; it is how the defect was found.

What is given up is real and worth naming. A host-rendered consent prompt is the one thing a model
cannot forge — it can print a convincing approval dialog into a chat, but it cannot make the host
draw one. On surfaces that implement elicitation properly, that was genuine protection against an
orchestrator manufacturing consent, and it is now gone everywhere rather than on some hosts. The
trade is accepted for two reasons. Enforcement was already surrendered in 0002, so the trust model
already assumed an orchestrator that does not lie. And a guarantee that varies by host without
announcing itself is worse than none, because it invites exactly the reliance it cannot support: the
0.7.0 flow told the user their approval was collected by the system, on a surface where it was not.

What is gained beyond the repair: the plan is no longer confined to a text blob composed by the
server. The orchestrator presents it however its own host presents things best — an artifact, a
widget, a canvas — which is what the unbuilt `canvas` profile was reaching for through a protocol
extension no host negotiates. Going around the protocol gets there today.

The consequence to hold on to, because nothing else will: `approved` in the run state is an assertion
by the orchestrator that it asked and was told yes. `skills/forge/SKILL.md` carries the rule; no code
in this repository can check it.
