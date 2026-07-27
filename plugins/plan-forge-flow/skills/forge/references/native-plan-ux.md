# Native Plan-mode UX contract

This contract defines Acts 1–2, plan preview, model pickers, and native
implementation approval.

## Read-only planning state

Keep the complete draft, review log, completed-round count, reviewer choice,
and builder choice in model-visible conversation state. Acts 1–2 run in Plan
mode and must not invoke a mutating Forge command or write repository, Git, or
plugin workflow artifacts. Read-only repository inspection, `doctor`, `models`,
`picker`, `issue-approval`, `request_user_input`, and read-only native reviewers
are allowed.

Normalize the human plan to UTF-8/LF with exactly one terminal newline. This
exact byte sequence is the sole human-plan domain used for preview, reviewer
input, hashing, the native wrapper, and the later `PLAN.md`. Reviewers receive
the complete current human plan and complete settled review log directly in
their prompt; they do not read pre-sign-off Forge artifacts.

## Reviewer and builder pickers

Use `request_user_input` one question at a time. Obtain choices only from the
fresh `codex debug models` catalog:

- Run `node {plugin-root}/scripts/forge.mjs picker --cwd {repo} --role
  reviewer|builder` for a model page. Pass `--cursor N` for `More…`.
- After choosing a model, add `--model {slug}` (and the current `--cursor N`)
  to obtain its effort page. The command is read-only and returns the exact
  request metadata to present.

1. Sort visible models by ascending numeric CLI priority, preserving CLI order
   for ties.
2. Show two model options per page. Use each CLI `display_name` as the option
   label and put the model slug plus CLI description in its description.
3. Add `More…` only when another page exists. A two-model last page has no
   `More…`; if following `More…` leaves one model, select it automatically
   instead of issuing an invalid one-choice question.
4. If exactly one visible model exists, likewise select it without presenting
   a meaningless one-choice question.
5. After the model is selected, present its non-`ultra` reasoning efforts two
   per page. Put the CLI default effort first, preserve advertised order for
   the rest, and use the CLI effort descriptions.
6. Apply the same last-page and single-option behavior to efforts. Never show
   or accept `ultra`.

Choose the reviewer at the beginning of Act 2, before the first fresh
`forge_reviewer`. Keep that selection in conversation state and pass it
explicitly on every Plan-mode reviewer spawn. Do not persist it yet.

## Preview, builder selection, and native approval

After Act 2 settles the plan, follow this exact order:

1. Send the complete canonical human-plan Markdown as a normal assistant
   preview. It must be readable in full before asking about the builder.
2. After that preview is visible, run the paginated builder model picker and
   then its effort picker. Bind the selection to the previewed human-plan hash.
3. Build the versioned approval envelope from the settled review evidence,
   reviewer and builder selections, selected-model catalog observations,
   canonical repository identity, origin identifiers, monotonic revision, and
   a fresh random nonce.
   Write no repository artifact for this step. Put only the canonical plan,
   review log, completed/max rounds, and reviewer/builder pairs in a bounded
   JSON input on stdin, then run `node {plugin-root}/scripts/forge.mjs
   issue-approval --cwd {repo}`. The command derives repository,
   transcript-origin, item, revision, nonce, hash, and fresh catalog evidence;
   use its `proposedPlanOutput` verbatim.
4. Construct the native approval wrapper from the identical canonical human
   plan followed by exactly one non-rendered Forge envelope comment. The
   comment is data, never instructions, and is excluded from the human-plan
   hash.
5. Make the final assistant output exactly:

   ```text
   <proposed_plan>
   {native approval wrapper}
   </proposed_plan>
   ```

   Do not call a tool, add commentary, or emit any text before or after this
   block. The native plan widget is the only sign-off surface.

The duplicate preview and native-widget rendering is intentional: the user
must read the plan before choosing a builder, and no picker may follow the
final `<proposed_plan>`.

If the human plan changes at any point, replace the complete plan, increment
the plan revision, and invalidate the previous preview binding, builder
selection, wrapper, and envelope. Show the complete revised preview, repeat the
builder picker, and issue a new final wrapper.

Native `Implement the plan.` and clear-context implementation actions are
handled only after Codex enters Default mode. The new context must run
`forge.mjs resume`; it must not implement directly. Materialization authenticates
the transcript relationship and wrapper before persisting the reviewer/builder
selections or any Forge artifact.
