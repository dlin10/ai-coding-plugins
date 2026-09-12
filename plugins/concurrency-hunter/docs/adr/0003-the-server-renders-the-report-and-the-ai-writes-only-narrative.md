# The server renders the report; the AI writes only the narrative

`cache-detective` has the agent write the whole report from `get_evidence`, on the principle that
prose belongs to the agent. That works for tens of findings and fails for the hundreds a legacy
audit produces: every evidence chain has to be read into the model's context and written back out,
twice the tokens for text that needs no intelligence, and the 30-minute deadline pays for it.

The server renders `report.md` deterministically: status, coverage, diagnostics, every finding with
both code paths, alias and overlap evidence, protection analysis, the event skeleton of the
scenario, the fingerprint, and the suppressed summary. The AI supplies only what the server cannot
write: the executive summary and, per finding group, the narrative — the interleaving in words, why
the protection found is insufficient, and fix suggestions marked `verify manually`. Each fragment is
submitted through a tool that validates it on arrival: every evidence id it cites must exist, every
location must be one the finding carries, the mark and the manual checks must be present; a fragment
that fails is rejected with the reason and may be resubmitted once. The report validator of the SPEC
therefore checks the AI's text only, which is the part that can be wrong.

The unit of narrative is the group, never the finding, because the report already collapses one
semantic cause into one group with representative locations and an occurrence count. Narrative is
mandatory for High and Medium groups; a Low group receives one if the deadline allows and otherwise
keeps the server's skeleton with a note saying so, and the run stays complete. This is a rule about
what makes a report whole, not a budget: nothing is capped, and a weak finding's scenario is in the
skeleton already.

Rejected: the agent writing the report, for the token cost above. Rejected: narrative for every
group as a condition of completeness, because a legacy codebase would then be `Incomplete` for the
text beside its weakest findings.
