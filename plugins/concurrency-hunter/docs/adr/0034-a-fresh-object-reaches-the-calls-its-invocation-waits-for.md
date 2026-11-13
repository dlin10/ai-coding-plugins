# A fresh object reaches the calls its invocation waits for

## Context

Two accesses to fresh objects never pair (SPEC's suppression rule, the vocabulary's **Fresh object**): an access
that reaches an object directly through a value whose every definition is an allocation of the same body touches
an object no other invocation touches that way, and the accesses of one invocation follow one another. A route
through a parameter was excluded, so the rule ended at the body that made the object.

Analysis of library bodies at the call site
([ADR 0029](0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md)) met the shape the rule left out.
In the demo's `polly-nested-retry` case, `UnwaitedNestedRetryWorker` runs
`_retry.Execute(() => { var detached = SeedAsync(); })`, and `SeedAsync` awaits `_seed.ExecuteAsync(...)`. Every
call of Polly 7's `AsyncPolicy.ExecuteAsync(Func<Task>)` makes its own `new Context()` and hands it down:

```csharp
public Task ExecuteAsync(Func<Task> action)
    => ExecuteAsync((ctx, ct) => action(), new Context(), ...);

public async Task ExecuteAsync(Func<Context, CancellationToken, Task> action, Context context, ...)
{
    SetPolicyContext(context, out ..., out ...);
    try { await ImplementationAsync(action, context, ...).ConfigureAwait(...); }
    finally { RestorePolicyContext(context, ...); }
}
```

With Polly's bodies admitted and its records withheld, 14 findings stayed on `Context`: 20 pairs of
`SetPolicyContext`, `RestorePolicyContext` and the unknown effect of the retry engine's sleep-duration provider,
between passes of the outer retry. Each pair is two calls' objects.

Nothing else could remove them. A region cannot tell the calls apart: they are the passes of one call site, and an
allocation site in one context is one region however often it runs; the places of ADR 0029 split call sites, not
calls. Ordering cannot remove them: the outer retry does not wait for the work it starts. `TreeConfined`
([ADR 0020](0020-a-tree-confined-region-pairs-within-one-run-of-its-creator.md)) confines the object to one run of
the worker, and the passes are inside that one run.

Every one of those accesses reaches the `Context` through a parameter of calls the creating invocation waits for:
the synchronous `SetPolicyContext` and `RestorePolicyContext`, `await ImplementationAsync(...).ConfigureAwait(...)`,
three methods that return the task of the one call they make, and the delay wrapper lambda the retry engine runs
itself, whose unknown call is handed the parameter. Program code has the same shape:
`var hook = new Hook(); _repository.Items.Add(hook); _repository.Mark(hook);` makes `Mark`'s write pair with itself
across two requests, although each request marks the hook it made.

## Decision

A value is handed down to a body by a call the invocation that holds it waits for: the value is an argument of the
call or the object the call is made on, and the call's work is done before the invocation goes on:

- a direct call, or a delegate the invocation runs in place, into a body that runs during the call — not an iterator,
  whose body runs where it is enumerated — and neither suspends nor returns a task it forwards;
- an async call, or a call to a **Task forwarder**, awaited right at the call (`ConfigureAwait` included);
- the call whose task a Task forwarder returns, when the forwarder made the value or was itself called one of these
  ways.

A forwarder's work is the work of the call it forwards, so a call to one is waited only as an async call is:
`F(o);` with its task dropped leaves `G(o)` running while the caller goes on, although `F`'s own body has returned.
An access reaches a fresh object when it reaches the object directly
through a value that holds only objects one invocation created, in its own body or in a body that invocation handed
the value down to, call within call, at any depth. The two reasons of the rule still hold: two invocations never
reach each other's fresh objects that way, and the accesses of one invocation follow one another, because each call
on the way is done before the invocation goes on. The unknown effect of a call handed such a value touches the
value's own fields as a fresh object's, as it does for a value of the body's own allocation.

The fact belongs to a method instance and the calls into it, not to a body: a body that may also be handed a value
of another origin — through a call that does not wait, a value loaded from a field, or a context merged under a
limit — gets no fresh value there. The proof is grounded in the allocation: a slot is fresh only through a chain of calls
that starts at the invocation that made the value. A slot the program's entry binds — a root's receiver, a parameter the
host injects — is not fresh, and neither is one that is fresh only if a recursive call handing the body its own value back
already is: a cycle of calls proves nothing. Recursion that hands down an allocation of its own is ordinary. It holds for
every body, the program's own and a library's.

Not handed down: a value a blocking wait follows, a call awaited later, a spawn and the runs of a library model's
delegate. Each would need its own argument that the work is done before the invocation goes on.

Rejected:

- **A region for each call.** The calls of one site in one context are one region; telling them apart statically
  needs a count of runs the analysis does not have.
- **Ownership confined to one invocation**, `TreeConfined` for one invocation of the creating body. It needs order
  between the executions of one invocation, and two of the 14 pairs — `RestorePolicyContext` in the root when the
  awaited call completes at once, against `RestorePolicyContext` in the continuation — are paths that exclude each
  other, not paths in order. Happens-before cannot say "one or the other", so they would stay.
- **Library bodies only.** The program has the same shape, and one question would have two rules.
- **Parameters without the receiver.** `Helper(hook)` and `hook.Mark()` hand down the same object.

## Consequences

- With Polly 7 bodies admitted, the 14 `Context` findings of the demo go. A finding between two executions of one
  invocation, or on anything reached through a holder, a capture or a field, stays.
- A write through a parameter or receiver of a waited call on an object its caller made no longer pairs across two
  invocations, in program code as in library code. Every finding of the demo or eShop that moves is named in the run
  record; the fresh-object skip counts of the snapshots may move where a pair a later check skipped is now skipped
  as fresh.
- SPEC's suppression rule and the vocabulary's **Fresh object** and **Call-site analysis** state the rule.
- What unseen code keeps is not seen, as before: an opaque call handed a fresh object is read as touching it there and
  then ([CONTEXT](../../CONTEXT.md), **Opaque call**), so a race through an object it keeps and touches later from
  another execution is missed.
