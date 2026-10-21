# Forge reference: asking the user

Every question you put to the user during a run follows this file.

## Asking the user

Every question you put to the user follows this section unless the user asks otherwise: the
interview, the Scout, critic and builder selections, the instruction questions, decisions on
findings, the round cap, narrowing a code-review round, approval, and the values a gate needs. It
belongs to forge and holds whichever skill is running the interview.

Ask one question per call of the host's question tool — `AskUserQuestion` in Claude Code — or per
message where the host has none, and wait for the answer before you ask the next. Work out the
frontier of the decision tree — every decision that could be asked now — but ask it one question at
a time: first the one the others depend on, and the next only once that one is answered, because an
answer can change or remove the questions behind it. This overrides the `grilling` rule to ask the
whole frontier in one round: a delegated interview skill supplies the questions, never their
batching. You make two batches unasked, one per worker role, both described under "Choosing the
vendor and model" in `interview.md`: once the role's vendor is answered, its model-and-effort, Fast and instruction
questions share one call, the critic's first. Their options come from the chosen vendor's
catalogue, each naming what it depends on, and answers that do not fit together get one follow-up
question. Explain each question of a batch before the call. If the user asks for questions in a
batch, batch them as they asked.

Before each question, explain it in the chat:

1. **The problem it settles** — what the code or the plan does now, and what breaks or stays
   unclear if nobody decides. In plain words: an internal ID — a finding's `F-0012`, a round
   number, a class name — appears only beside what it stands for, and every term the user has not
   met yet gets a one-sentence definition.
2. **An example**, when the question is about how code behaves — a short fragment from the
   repository with its `path:line`, the path taken from the repository root, or a minimal
   illustrative one labelled as such — and what happens to it under each answer.
3. **The options** — for each, what it means, what it changes in the plan or the code, its
   advantages, its drawbacks and its cost; then the one you recommend, and why.

The question offers the same options. Keep their descriptions short and in agreement with the
explanation, put the recommended option first, and end its label with "(Recommended)". A mechanical
question — a vendor, a model and effort, the Fast tier, a value a gate needs — needs one or two
sentences of explanation and no code.
