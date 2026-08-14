---
name: explain-code-simply
description: Explain code, errors, architecture, and C#/.NET concepts in clear everyday language matching the user's language without losing technical accuracy. Use when the user asks to "ELI5", simplify, explain how code works, trace execution, decode an exception, understand an unfamiliar symbol or pattern, or compare technical concepts at a beginner-friendly level.
---

# Explain Code Simply

Explain for an intelligent developer who is new to this specific concept. Be clear, compact, and respectful; never use baby talk.

Reply in the language the user used to address you. Preserve code identifiers verbatim, and keep an established English technical term in parentheses when translating it helps the user recognize the concept elsewhere. If the user explicitly requests another language, follow that request.

## Build the explanation from evidence

1. Read the relevant code before explaining it.
2. For nontrivial C# symbol relationships, use available Roslyn tools to inspect definitions, references, callers, and implementations before relying on text search.
3. Separate what the code proves from assumptions about runtime behavior or intent.
4. If essential context is unavailable, name the missing piece instead of inventing it.

## Explain in this order

1. Start with one sentence answering: **What does this do?**
2. Give a small mental model in literal language.
3. Walk through the execution flow using the code's real identifiers.
4. Show one tiny concrete example with actual input and output values.
5. Mention the most relevant side effect, failure mode, or gotcha.

Stop when the user can reason about the code. Add deeper detail only when it changes that understanding.

## Adapt to the subject

- For a single expression, explain inputs, transformation, and result.
- For a method, explain the happy path first, then important branches and side effects.
- For a class or service, explain its responsibility, collaborators, and lifecycle.
- For architecture, trace one realistic request end to end rather than listing every component.
- For an error, translate the message, identify where it originates, and explain the condition that triggers it. Do not propose or implement a fix unless asked.
- For comparisons, use a compact table only when three or more exact differences matter.

## Handle C# and .NET precisely

Call out hidden behavior when relevant, especially:

- `async`/`await`, tasks, cancellation, and exception propagation
- dependency injection lifetimes and object ownership
- LINQ deferred execution and repeated enumeration
- nullable reference types versus possible runtime `null`
- `IDisposable`, `using`, and resource lifetime
- value versus reference semantics
- middleware or request-pipeline ordering
- thread safety, synchronization, and shared state

Translate each concept into plain language, then retain the correct technical term so the user can recognize it elsewhere.

## Use analogies sparingly

Explain literally first. Use at most one short analogy when it makes the mechanism easier to remember, and state where the analogy stops matching reality if that limitation matters.

## Default response shape

Use this structure when it fits; omit empty sections:

```markdown
In one sentence: ...

How it works:
1. ...
2. ...

Example: ...

Watch out for: ...
```

Do not bury the answer under terminology, repeat the code line by line, add unrelated history, or modify code unless the user asks for a change.
