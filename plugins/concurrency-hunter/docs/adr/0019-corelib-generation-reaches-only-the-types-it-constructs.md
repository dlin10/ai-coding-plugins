# CoreLib generation reaches only the types it constructs

The model generator stops a member whose reachable set passes 1,500 bodies (TD-034b). On
`System.Private.CoreLib` 8.0.31 the reachable set's own call graph, with every body rooted and a virtual
or interface call reaching every override the class hierarchy offers, has one strongly connected
component of 8,253 bodies (8,036 members); the next has 13, and 12,859 members lie above it. Of the
16,612 public members, 3,827 take a delegate or a value that can hold a user object: 681 lie below the
component, 1,026 in it and 2,120 above it. Dispatch holds the component together. Without the dispatch
edges the largest component is 711 bodies, the resource strings of `System.SR` and the `ResourceManager`
behind them; without type initializers, without delegates, or without the dispatch of `Object` and
interface members alone it stays above 6,000. A driver drags the component in even for a member whose
own closure is one body: its setup calls base constructors that throw with an `SR` message, and its
seeds are enumerated through `IEnumerable`. Of 18 sampled candidates, 2 generate; the others reach
about 11,500 bodies.

The decision, for the generation of `System.Private.CoreLib` only:

- A virtual or interface call reaches only the overrides of types the run constructs, by a constructor
  call in a reached body or by a construction the reachable set starts itself, and of a value type only
  once a reached body boxes it. An override whose type is constructed later is reached then.
- The bodies of `System.SR` are never entered.
- A body outside that set may still run, so a call the heap resolves into one is an opaque call: an
  unknown effect on what its receiver and arguments reach, and a delegate handed to it runs in an
  unknown execution. It never resolves to nothing.
- A member one of the engine's recognizers claims is refused with `engine-recognized`: in the analysis of
  a program its calls are the recognizer's, so a model of it would never be read. Every other member is
  generated. Inside the run CoreLib's own types are source, and the recognizers, which know only types
  from metadata, do not apply to them: a call of `Monitor`, `Interlocked` or `Task` there runs its body.
  Any other assembly is still refused whole when a recognizer claims one of its methods, since a claim by
  name cannot tell its own type from a namesake there.

The consequences to hold. On the same sample 11 of 18 candidates generate, each within 2 to 96 bodies and
3 to 8 s. The other 7 reach about 6,500 bodies through the runtime's own core, reflection with
`Reflection.Emit`, globalization and formatting, which constructs hundreds of types. Generating all 3,827
candidates confirmed the split: 1,990 were analysed within 2 to 376 bodies, 443 of them giving a model, and
1,837 reached 6,400 to 8,053 bodies; no member fell between. The analysis of a
program, the generation of a package and the `generate` command, which still answers `corelib` for a
CoreLib member, are unchanged. A member that hands a user object to an exception message gets an unknown
effect on that object through `SR.Format`. A constrained call on a value the run creates but never boxes
reaches no override, so the heap's resolution on that value is an opaque call where class-hierarchy
dispatch would run the body.

Rejected: generating bottom-up, the models of lower components replacing their bodies. 82% of the
candidates lie in the component or above it, and a driver reaches it through `SR` and dispatch even from
below. Rejected: one access record for many executions, with pairs built over sets of executions. A
generation stops at the closure bound before any access is collected, so the cost it removes is not the
one in the way. Rejected: reaching a body lazily once the heap resolves a call into it. It is exact, but
it is a fixpoint between the reachable set and the heap; it stays an open question. Rejected: serving
`SR` through models of its own assembly. A library model never applies to a member declared in the run,
and changing that is where bottom-up generation would start.
