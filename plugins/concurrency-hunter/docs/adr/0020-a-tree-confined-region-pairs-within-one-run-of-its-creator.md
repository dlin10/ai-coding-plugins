# A tree-confined region pairs within one run of its creator

Ownership is decided per region, and one abstract execution stands for every run of a root: every request
an HTTP action serves is the same execution, and so is every run of the work it spawns. A region the
action creates and touches only itself is `ThreadConfined` and pairs with nothing. Once the action hands it
to work it starts, the region is `Escaped`, and the engine pairs one request's work with another request's,
although each request made its own object: `var box = new Box(); await Task.Run(() => { box.N++; }); return box.N;`
gives DCA1002 (the lambda against itself) and DCA1001 (the lambda's write against the read after `await`) on
`alloc:PostController.Post()#Box` `N`, while the same body with `List<int>.ForEach` running the lambda in
place gives nothing. The happens-before path through the `await` does not hold either, because a spawn or a
join is trusted only inside an instance tree whose root runs once, and an action repeats. Variables a lambda
captures become locations of their own, a closure the declaring member's invocation creates, so they would
inherit the same false pairs: `var count = 0; await Task.Run(() => count++); return count;`.

The decision: a region one run of its creator confines is paired as if that run were the only one.

- A region — an object or a closure — that one execution creates is `TreeConfined` when executions other than
  its creator access it, every one of them started from the creator at any depth — by a spawn, an async call's
  tail, a thread, a timer, a `Parallel` body or a continuation — and no shared root reaches it by the escape
  rules of TD-052. A region only its creator accesses stays `ThreadConfined`, and one several executions create
  is classified as before. Storing the object, or a delegate that captures the closure, where a shared root
  reaches it publishes the region; copying a captured variable's value there publishes nothing.
- For two accesses to a `TreeConfined` region, the creator counts as running once. Two executions of its
  tree overlap as they would in an instance tree whose root runs once: an execution runs once per run of the
  creator only when every start on its chain from the creator runs its work once and stands where one run
  of its parent passes once, so a child of work started in a loop, a `Parallel` body, a periodic timer, a
  callback that may be called again and recursion all overlap themselves; and happens-before edges hold
  along such once-running chains, spawn, join and continuation alike. Accesses of two runs never pair: they
  cannot reach one object.
- When the creator is itself an execution that may run several times in one run of its parent, a
  `Parallel` body say, one run of that execution is the tree.
- `TreeConfined` reduces no unknown effect to a read, unlike `ThreadConfined`: two executions of one run
  may run concurrently. For publication and for semantic gaps it is shared, as `Escaped` is.

The consequences to hold. A finding that pairs one request's spawned work with another request's on an
object, or a captured variable, that request made disappears; a finding between two executions of one
request on it stays, and so does one on anything a shared root reaches. Each finding of the demo or of
eShop that moves is named in the run record. TD-050, TD-051, TD-053 and TD-060 and the vocabulary's
**Ownership** and **Instance tree** state the rule.

Rejected: giving a captured variable the precision objects have and recording the cross-request pairs of
both as a question. The false pair is the ordinary shape of request code that fans work out over its own
data, and every closure the run makes a location would add one. Rejected: dropping only the self-pair of the
spawned work and keeping the happens-before rule as it is; the write in the work against the read after the
`await` would remain a false pair.
