# A collection's structure is a resource of its own

Phase 4 gives an access path a selector, so that `_slots[0]` and `_slots[1]` are two resources and
a pair between them never forms. Applied to a collection, that alone would be wrong in the
dangerous direction: two threads calling `Add` on a plain `Dictionary` with the constant keys `"a"`
and `"b"` corrupt it, however distinct the keys are proven to be, because both rewrite the same
buckets. Suppressing that pair would lose a real race, which is exactly what TD-070 was written to
prevent when it said element storage and collection structure are modelled separately.

So every collection region carries two resources. The **structure** is the access path to the
collection itself: its count, its slots and their order. The **element storage** is that path plus a
selector.

One member usually touches both, and modelling it on only one of them loses real pairs. A lookup
walks the buckets before it reaches a cell, so `d["a"]` reads the structure as well as the cell, and
an insertion that rehashes while that walk is in progress races with it even though the two keys are
proven distinct. So each member is given its full set of accesses over both
resources, and three of them are easy to get wrong. Removal writes the cell it removes, not only the
structure, because a cell going from present to absent is a change another execution can lose: a
`TryRemove` between a read of a cell and the write it feeds is undone by that stale write. `Clear`
does the same to every cell at once. And a member that shifts its neighbours — `Insert`, `RemoveAt`
and `Remove` on a `List<T>` — writes every cell from the affected index onwards, which a single
selector cannot express, so it takes an unknown one.

The rest follow from what the member actually touches. An insertion writes the structure and the
cell it lands in. An indexer get, and a `TryGetValue` that yields a value, read the structure and
read their cell. `Count` and `ContainsKey` read the structure alone, because a key lookup never
reaches the value. `Contains` on a `List<T>` is not the same member at all: it compares values, so
it reads every cell. Enumeration reads the structure and every cell. A `List<T>` indexer set writes
the structure as well as its cell, because it moves the version counter every iterator watches.

The two are independent, and which of them a collection makes safe is the collection's own
business. A `ConcurrentDictionary` is atomic on both: `GetOrAdd`, `AddOrUpdate` and `TryUpdate` are
atomic on the element storage of their slot, and every member that changes the structure is atomic
on the structure too — which is why its enumerator is safe to use while another execution reads and
writes, and does not throw. It is not a moment-in-time snapshot and never promised to be: what it
guarantees is that enumerating and mutating at once is defined behaviour, which is all the conflict
rule needs. A plain `Dictionary` or `List<T>` is atomic on neither.

This decides the rule for enumerate-while-mutate without adding one. On a plain collection,
enumeration reads the structure, insertion writes it, neither is atomic, and the existing conflict
rule fires. On a thread-safe collection both are atomic, no side is a non-atomic write, and no pair
forms — a tool that reported one would be reporting the class's whole purpose as a defect.

DCA1004 stays what section 7 says it is, a lost update, and is not widened to cover a case that is
not one. What section 7 does need is for a check-then-act sequence to count as the non-atomic
read-modify-write it plainly is, over whichever resource carries the dependency from the check to
the act: the element storage for a lookup keyed by a selector, the structure for a `Count` that
decides an `Add`.

Phase 4 models `List<T>`, `Dictionary<TKey, TValue>` and the concurrent collections of TD-085 by
their members, and nothing else; the general library semantics table of TD-034a stays in phase 5,
where escape through collection insertion is decided with it.

Rejected: one resource per collection. Proven-distinct keys could then never remove a pair, and
every pair of `GetOrAdd` calls on a `ConcurrentDictionary` would be reported — the class's whole
purpose, reported as a defect. Rejected: element storage only, with the structure folded into the
element resource under an unknown selector. It reads like the same thing and is not: a pair of
writes under proven-distinct selectors would be dropped before the structural conflict was ever
considered.

The consequence to hold: a finding on a collection must say which of the two resources it is about,
and the remedy follows from the operations rather than from the resource. A pair of check-then-act
sequences needs the check and the act inside one critical section, and swapping in a thread-safe
collection does not help at all — a `Count` that decides an `Add` is already two atomic calls with a
gap between them. A read paired with a write needs both sides under one primitive, because guarding
the write alone leaves the read unguarded. Only a pair of plain non-atomic operations on one cell is
answered by an atomic member.

## Amendment, phase 5b: `HashSet<T>`, `Queue<T>`, `Stack<T>` and `LinkedList<T>`

Until phase 5b these four were opaque, so what an opaque member put into them was held by nothing
the heap knew: a deep read of the collection never reached its elements, and neither did anything
else. Phase 5b models them by their members with the same two resources. None of them is
thread-safe, so none of their members is atomic on either resource, and none has an index, so every
cell is the unknown one.

The members follow the rules above. An insertion — `Add`, `Enqueue`, `Push`, `AddFirst`, `AddLast`,
`AddBefore`, `AddAfter` — writes the structure and a cell, and the collection holds what it
inserted. A removal — `Remove`, `Dequeue`, `Pop` and their `Try` forms, `RemoveFirst`, `RemoveLast`
— writes the structure and a cell; `Clear` writes the structure and every cell. `Peek` reads the
structure and a cell, `Count` the structure alone, and `Contains`, `Find` and enumeration read the
structure and every cell, because they compare or visit values. A `LinkedListNode<T>` is a cell of
the list it belongs to: the members that hand one out — the insertions above, `First`, `Last`,
`Find`, a node's `Next` and `Previous` — read the structure, and a node's `Value` reads or writes
that list's cell without touching the structure, because setting it moves no version. What a
removal or `Peek` returns points to no object yet, like a `List<T>` indexer, until the heap stores
collection elements (open question 30).

