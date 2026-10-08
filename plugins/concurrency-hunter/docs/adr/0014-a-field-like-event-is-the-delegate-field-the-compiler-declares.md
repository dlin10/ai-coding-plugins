# A field-like event is the delegate field the compiler declares

Until phase 5d run B2b an event subscription, `x.E += h`, was an unsupported operation: the handler
was handed to nothing, and its body was never analysed — in user code and in the decompiled library
code the **Model generator** reads, where nearly every event is field-like and its `add` and
`remove` are written by the compiler, with no body the engine could follow. The generator could not
see where a library raises an event, and a user's handler ran nowhere. Combining delegates was blind
the same way: `a + b` on delegates was a computation pointing to nothing, so `_onDone += h;
_onDone();` was a dispatch with no receiver, and `h` never ran there.

The decision is that events are what the compiler makes of them, and that delegate combination has
one rule:

- Subscribing and unsubscribing are calls of the event's `add` and `remove` accessors, by whatever
  route the call takes — direct, virtual or through an interface. The accessors of a library event
  have no body in a run, so a **Library model** describes them or the call is opaque: the handler is
  handed to a call without a body, as any delegate is. An event with accessors of its own runs their
  bodies.
- A field-like event is the delegate field the compiler declares for it, and its accessors have the
  bodies the compiler writes: a compare-and-swap loop that stores the combination of the field's
  value and the handler, or the field's value without it. Subscribing is an atomic read-modify-write
  of that field; raising it — `E(…)`, `E?.Invoke(…)`, a copy of it invoked — reads the field and runs
  every delegate it may hold, in the raising execution; assigning `E = …` inside its type writes it.
  Those accesses pair into findings as any field's do, and only a non-atomic write conflicts
  (TD-072): `E = null` racing a raise or a subscription is a finding; a raise racing a subscription
  or an unsubscription is not, and neither are two subscriptions.
- Combining delegates — `a + b`, `+=` on any location, `Delegate.Combine` — gives a delegate that may
  run every delegate either operand may run; removing — `a - b`, `-=`, `Delegate.Remove`,
  `Delegate.RemoveAll` — gives one that may run every delegate the left operand may run. Removal
  removes nothing, as nothing ever leaves kept storage.
- `System.Timers.Timer.Elapsed` stays the timer recognizer's: its subscription is recognized once and
  runs the handler once.

Rejected: an event as a carrier, not a resource — a subscription that puts the handler into the
event's storage with no access, a raise that runs everything stored. It is simpler, but it is not
what the compiler emits, and it can never find a race on the event's own field. Rejected: lowering
only library events, leaving the events of source code to phase 5g (question 62 of
`docs/QUESTIONS.md`). It is cheaper, but generated models of event accessors would be empty — a
decompiled field-like event hands its handler to an accessor with no body — and user handlers would
stay unanalysed. Rejected: the rule for event fields only. Ordinary delegate fields and locals would
keep losing what `+=` adds, and two places would decide one question differently. Rejected: tracking
removal, so that a handler removed with `-=` no longer runs. It needs to know that the handler was
removed on every path, which a may-analysis does not know.

The consequences to hold. A handler that was removed still runs wherever the event is raised later —
an answer wider than the truth, never narrower. A raise written `if (E != null) E(…)`, which reads
the field twice, can throw when an unsubscription empties it in between; the rules report no such
race, because each read is a read and the unsubscription is atomic. User code that combines delegates now runs them
where it invokes the combination, so findings can appear in code that had none, through handlers the
analysis never ran before. Whether a library event runs its handler in the caller's execution or on
a thread of its own is said only by the accessor's model; ADR 0015 decides what the generator may
write there.
