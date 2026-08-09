# Native plan contract

The registered Cursor `.plan.md` is the approval surface. Resolve and record its exact canonical path. It must be a bounded regular file inside Cursor's native plan storage, not a symlink or reparse-point path.

Place exactly one marker in the file:

```html
<!-- plan-forge-flow:run=<run-id>;workspace=<scope-id> -->
```

Immediately after it, include this visible preamble with the actual canonical workspace, run ID, and installed launcher path substituted:

```markdown
> **Plan Forge execution gate (advisory):** Before any repository write, run Plan Forge `plan materialize --host cursor --workspace "<canonical-workspace>" --run-id "<run-id>"` using the installed launcher. Stop if it does not succeed. Cursor can bypass this instruction; approval enforcement is advisory.
```

Then include the plan title, an exact `## Approach` section whose implementation
tasks are numbered `1..N` in order, verification, and scope exclusions. Keep each
numbered task decision-complete because Plan Forge dispatches it as one unit. Do
not add another Plan Forge marker.

The CLI hashes the entire canonical file: marker, preamble, and body. Canonicalization removes one UTF-8 BOM, converts CRLF or CR to LF, and ensures exactly one final newline. No other change is ignored. Editing wording, whitespace, tasks, marker, or preamble after review invalidates approval and requires `/forge resume`, restaging, a fresh review, and finalization.
