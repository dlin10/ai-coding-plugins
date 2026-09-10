# Review the final tree from the run's baseline commit

`forge.review.code` used to compare the working tree with `HEAD`. That described the uncommitted
tree, not the work produced by a Run: once the Orchestrator committed a phase mid-run, `HEAD` moved
past it and the next round could approve an empty diff without a Critic seeing the committed work.

The code-review **Review window** is the net difference from the commit recorded by `forge.begin` to
the current working tree, including tracked and untracked files and using the same documentation
pathspec as the sensitive-path guard. It deliberately judges the final state rather than the history
of edits: dirt already present when the Run began remains in view, while a change later reverted to
the baseline state does not. Baseline capture and drift reporting keep their existing `HEAD`
comparison because they answer a different question — whether the tree moved during the interview.

The recorded commit is used only while it is an ancestor of the current `HEAD`. A valid commit made
unrelated by a rebase, amend or branch switch falls back to a current, resolved `HEAD`; using a merge
base would pull unrelated history into review, while refusing would make a recoverable repository
operation end the Run. The fallback is named in both the Critic prompt and the returned summary,
including an empty-window approval, because it can omit committed work. A missing or unresolvable
baseline is corrupt Run state instead and stops review with an instruction to begin a new Run.

The Git adapter resolves the base commit once and returns the changed paths, content diff and window
identity together. The two Git reads remain lock-free and may observe a concurrent working-tree edit
between them; freezing an index or temporary tree would add coordination and repository state that
the rest of the Run deliberately avoids. What remains load-bearing is that both reads use the same
base commit and pathspec, so the sensitive-path guard covers the same semantic window sent to the
vendor.
