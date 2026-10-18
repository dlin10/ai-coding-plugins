---
status: accepted
---

# Give an oversized review window to the critic by path

Supersedes the `untrackedByReference` half of
[0029](0029-refuse-an-oversized-review-prompt-and-let-the-orchestrator-narrow-it.md) and its
rejection of tracked files by reference. The measurement, the refusal and `excludePaths` stand.

Under 0029 a round over its vendor's limit was refused, the orchestrator showed the user the sizes,
and the user chose how to narrow it. Two runs later the answer was always the same. Run
`20261012-163837-6fc7b1` (A9) and run `20261017-180043-675224` (B3) each refused round 1 — B3's prompt
was 1,279,723 characters against codex's 1,048,576 — and each was started again with
`untrackedByReference: true`, which leaves the window as it is and changes only how it travels. The
question cost a round trip to the user and decided nothing the server could not.

The critics given files by path read them. In all three B3 rounds the codex critic's tool calls in
`forge.log` named each of the 30 untracked files it was pointed to.

The ceiling is also close for what 0029 kept in the prompt: B3's tracked diff alone was 679,759
characters, `docs/SPEC.md` and `docs/QUESTIONS.md` 87k each, because their paragraphs are single long
lines and a changed one appears twice.

## Decision

**The server chooses how the window travels, by the vendor's limit, in three steps.** It composes
the prompt with every file embedded. Only when that is over `IVendor.PromptCharacterLimit` does it
compose again with the untracked files given by path, as 0029's argument did. When that is still over,
it gives the tracked files' diffs by path too. When even the prompt holding none of the window's
content is over, the round is refused as before, and the refusal advises only `excludePaths`. Nothing
is asked: the window is the same in every step, and what the critic is handed is still the user's call
only where `excludePaths` narrows it.

- **Untracked files** stay where they are: the prompt lists each one's path from `workspaceRoot` and
  its line count, and the critic reads it from the working tree.
- **Tracked files' diffs** are written by the server, one file per changed file, under the run
  folder's `review-round-<N>/`, named by the file's path from `workspaceRoot` with `.diff` appended.
  The prompt lists each changed file, its added and removed lines and the absolute path of its diff.
  A diff holds git's block for that file, exactly what the embedded prompt would have carried. The
  folder is emptied before it is written — a round the server died in is retried under the same
  number — and deleted when the round ends, whether the critic answered, failed or was cancelled; a
  refused round writes nothing.
- The critique's window summary, and therefore the flow log, says how many files of each kind were
  given by path and why.

The secret guard reads the prompt with everything embedded, before any step is chosen, so a file
the critic reads off disk has passed it exactly as an embedded one would.

`untrackedByReference` is gone from `forge.review.code` and `forge.work.start`.

## Why a file per changed file

One diff for the round would have been 680k characters in B3. A critic reads a file in pieces —
claude's Read stops at 2,000 lines — and a reader paging through one long file is the reader most
likely to skim it. One file per changed file keeps each read whole for all but the largest changes,
and lets the prompt's list double as a table of contents with sizes.

## Why the run folder

It ignores itself (`.forge/.gitignore`), so the diffs never widen a later window, and writes there are
already contained by `RunDirectory`. It may lie outside `workspaceRoot` when the host's session is
elsewhere (docs/adr/0011), which is why the prompt gives absolute paths. Only codex states a limit, so
only a codex critic is ever pointed to them, and codex critics run without a sandbox
(docs/adr/0033).

## Considered and rejected

- **Always by path.** Claude and cursor-agent state no limit, so for them it would only trade a
  prompt that certainly holds the window for one the critic may skim. An embedded window is the
  better delivery whenever it fits.
- **By path, file by file, largest first, until the prompt fits.** It keeps the most text embedded,
  but which file the critic reads off disk would depend on its size rather than its kind, and a
  round would hand over a mix nobody chose.
- **A GitHub compare link.** It needs the working tree committed and pushed every round, network
  access for the critic, and the content published before the secret guard has read it.
- **Letting the critic run `git diff`.** A claude critic has no shell grant, and the blanket grant a
  builder has lifts claude's safety checks (`CONTEXT.md`).
- **Checking that the critic read what it was pointed to.** Tool calls name files only loosely — a
  search over a folder reads files it never names — and B3 gave no miss to test a check against.
  The critic contract and the prompt's instruction to read every file are the guarantee, as they
  were for 0029's untracked files.
- **Keeping the diffs after the round.** They would record what each round was handed, which the
  flow log does not, but the user chose not to keep them.

## Consequences

A vendor whose critic cannot read outside `workspaceRoot` and that states a limit would need the
diffs written elsewhere; none does today. A round given by path costs the critic tool calls the
embedded round did not, and its quality rests on the critic reading what it is told to. The
orchestrator no longer sees a refusal in the cases A9 and B3 met, and no longer passes anything for
them.
