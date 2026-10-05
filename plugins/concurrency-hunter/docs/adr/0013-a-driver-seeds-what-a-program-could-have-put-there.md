# A driver seeds what a program could have put there

Phase 5d run B1 taught the **Model generator** where a delegate runs. Run B2 has it write whole
**Library models**: what a member does to each argument, what it returns, keeps and assigns, and what
each delegate is handed. A model like that is only as complete as the **Driver** behind it, and a
driver builds each argument one way — one constructor, one factory, one canned value. A field that
way leaves empty is empty for the analysis, so a member that would reach a user object through it
shows nothing. `JsonSerializer.SerializeToUtf8Bytes(value, options)` with `new JsonSerializerOptions()`
reads an empty converter list, and a model written from that says the call does nothing to
`options` — while in a program a user converter in that list receives `value` and runs. The same
holds for state a library keeps in statics that a program replaces: `Console.SetOut` makes
`Console.Out` a user writer, and FluentValidation's `ValidatorOptions.Global` holds user delegates.
A model built on the driver's state alone is narrower than the truth while looking exact.

The decision is that the driver puts there what a program could have put there, and that the
generator says nothing rather than something partial:

- Before it calls the member, the driver adds a **Seed** to every field that could hold a user
  object — in the library objects it hands over as arguments or as the receiver, and in the library's
  static fields. A seed is an object of a class the driver declares for the field's type, and every
  member of it is a witness. The generator opens those fields in its own compiled copy of the
  decompiled library — `private` becomes accessible, and `readonly` is dropped on reference-typed
  fields only, since on a struct field it changes copying — so seeding is ordinary code in the
  driver's setup and the engine does not change. A seed only adds to what the field holds, so every
  such field is seeded without asking whether it was empty. It never hides the unknown: a field that
  also receives what a call without a body returned stays a place the analysis cannot see.
- A value the library hands a seed's member — or a probe's own `ToString`, `Equals` or
  `GetHashCode` — is a value user code received, and the model writes it as a **Deep read** of that
  argument. That is the approximation the built-in serializer and logger models already make for
  user getters, converters, `ToString` and `Equals`. A delegate handed to a seed runs in an
  **Unknown execution**: nobody knows what user code does with it.
- Every object the generator sees where an entry speaks — the result, what a keeper keeps, an `out`
  or `ref` argument, an array's cells, a delegate's parameter — is named by the model's language of
  values, or the member gets no model. An object the library made in the same call, or in the same
  call of a holder's member, and that holds no user object is named `new`, which a delegate's input
  may now be as a whole, as a result already could. What the open-world rule says of calls without a
  body holds here unchanged: a probe they reached leaves the member without a model.
- A member the generator gives no model is answered with no entry and a reason, never with an entry
  declaring it opaque. The generated layer stays silent about it, so a later layer — the AI models of
  phase 5e — may still describe it; the model evals count silence as opaque.

Rejected: refusing a model when the member reads a field the driver left empty. It is sound and needs
no seeds, but the serializer, `Console.Write` and every member that calls user code with its
argument stay opaque, which is most of what a team would want a model for. Rejected: refusing a
model when any reachable field of an argument is empty. It is simpler and loses members that only
write into that state — `RepeatedField<T>.Add` grows the array it keeps its items in. Rejected: a seed
input to the heap solver, pointing each empty field at a witness after setup. It seeds exactly what
is empty and leaves the library's text alone, but it changes the engine every run uses for a need
only the generator has, and it needs its own proof that the solver without seeds is unchanged.
Rejected: seeding arguments and the receiver but not statics. A static the library fills itself with
visible code and a program replaces would show the driver only the library's object, and the model
would be narrower than the truth without a single call it could not follow. Rejected: seeds whose
members are calls without a body. Nothing approximates there, but every member that calls back into
user code with its argument loses its model. Rejected: leaving an input the language cannot name as
the "nothing known" the vocabulary allows. It is how a hand-written entry may stay silent, but for a
generated one it drops what a user's lambda reads and writes through that parameter. Rejected: an
entry `opaque: true` for a member without a model. It records the generator's verdict in the
repository, but in the generated layer it would answer for the member and shut out the AI layer.

The consequences to hold. The compiled copy the generator analyses is not the library: opening a
field can, in rare code, change which member a name binds to, and a decompiled body that relied on
the field being private is analysed with the field written from outside. Seeds widen what a driver
reaches, so the 1500-body closure bound is hit sooner, and a library with many user-typed fields
seeds many witnesses. A seed's deep read misses what a user callback writes to the value it is
handed, exactly as the built-in models do; a model never proves protection or order (TD-034a).
