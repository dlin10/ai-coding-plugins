# Forge reference: reading through a subagent

Read it right after `forge.begin`, and again after a compaction.

## Why

Everything you read stays in your context until the host compacts it, and so does your reasoning
about what you read. Reading done by a subagent comes back as a short report; the files, the logs
and the reasoning stay in the subagent's context. Hand a reading to one whenever you need its
conclusion rather than its text.

## What to hand over

Read-only work whose answer is a list or a short finding:

- **The class, before a claim** — every caller, reference or implementation of a symbol, every
  member of a class a revision changes, every other place that decides the same rule: the listing
  *Writing a revision* in `plan-review.md` asks for before a patch, and the same listing in the
  sweep.
- **A test or gate log** — which tests failed, with their messages and `path:line`, and the gate's
  exit and first error.
- **Project documents during the interview** — the passages that bear on the questions you name.
- **The code a fact names**, when settling an Evidence-map flag needs more than a few lines read.

Keep a single lookup yourself: a few lines at a known place, or one reference query, cost less
inline than a brief.

Never hand over: a Scout answer, or the Evidence check over Scout's answers; writing or revising the
plan; a decision, a question to the user or a forge tool call; filtering the critic's findings; any
edit, which is the builder's work; any judgement of the plan or the diff, which is the critic's.

## The brief and the report

A subagent knows only its brief. Give it the question; the workspace's absolute path, and which
Roslyn server serves it and how to call it, as *The Scout question* in `scout.md` says; the scope
to search, beyond `src/` where behaviour is pinned elsewhere; and the report it returns:

- no edits, and no more than about 3,000 characters;
- every item with its `path:line` and that line quoted verbatim;
- whether the list is complete, and how it searched — "none found" names what was searched.

## The agent

On Claude Code use the `Explore` agent, which reads and cannot edit. On Codex use `spawn_agent` with
`agent_type: "explorer"` and `fork_turns: "none"` — left out, `fork_turns` copies your whole
conversation into the agent — and never a `worker`. Start it in the same message as a call you wait
on, such as `forge.work.poll` or `forge.plan.review`, or beside your own work, and take the report
at the first step that needs it.

## A report is a pointer

A report says where to look, as a Scout answer does. Before a revision, a decision or a question to
the user rests on a line the report names, read that line yourself: *Read before you write* in
`plan-review.md` holds for every claim, whoever found the place. A report that calls its list
incomplete, or gives an item no line, is no listing: ask again more narrowly, or run the query
yourself.
