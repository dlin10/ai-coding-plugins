# Native plan contract

Create the registered Cursor `.plan.md` only after the chat plan is reviewed and the pending run is `ready`. Native plan creation is the terminal action of that Plan turn. The file must be a bounded regular file inside Cursor's native plan storage, not a symlink or reparse-point path.

Place exactly one marker in the file:

```html
<!-- plan-forge-flow:run=<run-id>;workspace=<scope-id> -->
```

Immediately after it, include this visible preamble with only the actual canonical workspace and run ID substituted:

```markdown
> **Plan Forge execution gate (advisory):** Before any repository write, run Plan Forge `plan materialize --host cursor --workspace "<canonical-workspace>" --run-id "<run-id>"` using the installed launcher. Stop if it does not succeed. Cursor can bypass this instruction; approval enforcement is advisory.
```

Copy the preamble exactly. Do not append the absolute launcher path or otherwise change its wording.

Then include the plan title, an exact `## Approach` section whose implementation
tasks are numbered `1..N` in order, verification, and scope exclusions. Keep each
numbered task decision-complete because Plan Forge dispatches it as one unit. Do
not add another Plan Forge marker.

At the first Build materialization, the CLI discovers exactly one native plan by the run/workspace marker and validates its path, bounds, sensitivity, exact preamble, and `## Approach`. It then snapshots the current complete native file, including Cursor frontmatter, into the materialization transaction. The transaction hash protects atomic replay and `.forge/PLAN.md`; it is not approval evidence. The native body may differ from the reviewed chat draft, and later user edits do not require another plan review.
