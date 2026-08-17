# Run the code-review loop through the orchestrator, not inside one call

Reverses the loop half of the `forge.review.code` design: the tool ran the entire critic-to-builder
loop inside one call, on the premise that nothing in that loop needed the interview context. One
critic round per call replaces it, with a new `forge.review.fix` carrying the orchestrator's
filtered findings to the builder. The critic and the builder no longer talk directly.

The premise was disproved by running the flow on this repository. The critic demanded coverage of
staged and untracked files — a real gap, and pre-existing — but the approved plan explicitly
excluded it. Inside the sealed loop no participant could arbitrate: the critic does not know what
the plan settled, and the builder obeys the critic rather than the plan, so the diff grew with every
round and the loop diverged from approval instead of converging on it. The only participant who knew
the demand was out of scope was the orchestrator, and the design had locked it out. A second,
independent failure pointed the same way: two rounds took about as long as a host's idle timeout,
so the sealed call died before its own cap could fire and the mid-loop state explained itself to
nobody.

Two rules keep the filter from quietly becoming a censor. The critic now receives the approved plan
with the diff, plus a shared scope contract (`prompts/scope-contract.md`, appended at load time the
way the Roslyn contract is), so out-of-plan demands arrive as `minor` notes rather than blockers —
most scope disputes are prevented rather than arbitrated. And a deferral is a recorded decision,
not a deletion: `forge.review.fix` writes every deferred finding and its reason into the review
log, where the next round's critic reads it as settled and the user sees it when the review ends.

What is given up: the sealed loop could not be steered off course by a lazy or biased orchestrator,
and now the orchestrator sits exactly where findings can be dropped. That trade was already made
twice — enforcement in [0002](0002-mcp-server-surface-without-enforcement.md), approval in
[0003](0003-approval-through-the-orchestrator.md): the trust model assumes an orchestrator that
does not lie, and `skills/forge/SKILL.md` carries the discipline — pass through when in doubt,
defer only what the plan settles, report every deferral to the user. No code in this repository can
check it.
