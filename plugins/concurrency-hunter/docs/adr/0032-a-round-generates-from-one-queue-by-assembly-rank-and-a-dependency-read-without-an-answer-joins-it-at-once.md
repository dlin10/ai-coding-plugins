# A round generates from one queue by assembly rank, and a dependency read without an answer joins it at once

Status: Accepted

This decision amends the rounds of
[ADR 0027](0027-the-run-generates-in-rounds-and-a-generation-takes-its-dependencies-models.md) and the order of
generation in SPEC TD-034c. Finality, what a generation sees of its dependencies, when a member is generated again, the
cycle rule and the limits stay as ADR 0027, [ADR 0031](0031-the-recording-of-committed-generated-models-has-a-run-limit-of-its-own.md)
and TD-034c have them.

## Context

ADR 0027 answers a dependency that a generation read without an answer only in the next round. Every other member of the
same round that reads it before then reads no answer either, and is generated again once the dependency has a model.

- **What the repeats cost.** Issue #371 replayed the demo recording's traces exactly. All 55 repeats had one cause: the
  member's first generation read a dependency with no answer at all, which by the next round had a model. 50 of the 55
  dependencies were first read in the reader's own round. The repeats took 485 s of #244's run B, 16 % of its generation.
- **The clearest case.** Seven generations read `RouteValueDictionary.get_Count` in round 2 while it had no answer. It was
  generated in round 3 in 2.0 s, and all seven were generated again in round 4: among them the four `RoutePattern` members
  and `RouteEndpointBuilder.Build`, 251.5 s.
- **Ordering inside the round** saves about 15 s, because the dependency is still generated only in the next round.
- **What makes an earlier generation safe.** A generation's own assembly stays opaque to it, so a dependency is always in
  an assembly that the reader's assembly reaches through its references. The read graph's 54 assemblies are acyclic, and a
  repeat reads the same dependency set as the first generation.
- **The order also decides what a capped run answers.** TD-034c says the order decides only which members the budget
  reaches first. Under the 900 s run limit, that is which members a hunt answers. The recording's two-hour limit never
  binds. [ADR 0029](0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md), decided and not yet built, takes
  generation out of the hunt. Until then, the order of a hunt's generations matters.

## Decision

- **One queue per round.** A round's members wait in one queue: the targets of the scope's last analysis that have no
  answer, the dependencies that generations saw without an answer, and the members whose view of a dependency changed.
  The round takes the first member, generates it and publishes its answer, until the queue is empty.
- **A dependency joins at once.** When an answer is published, every dependency its generation saw with no answer joins
  the queue: no answer in the run, none in its file, and its implementation on disk. So does every member that saw the
  published member and now sees it differently, except a member whose views lead back to itself and one whose repeat the
  budget or a failure stopped. A dependency is therefore generated in the round that first reads it.
- **The rank.** A member's rank is the rank of its implementation assembly among the files its library compilation
  references: 0 for an assembly that references none of them, otherwise one more than the highest rank of an assembly it
  references. Assemblies whose references lead back to one another share one rank. A dependency is in an assembly that
  its reader's assembly reaches through its references, so it ranks below its reader when both resolve the same reference
  files. The rank decides only the order: a dependency that ranks no lower costs a repeat, not a different answer.
- **The order.** The targets of the scope's last analysis that have no answer come first, members handed a delegate first
  among them. Every other member comes after them: a dependency, and a repeat, a target's included. Within each group,
  members go by rank, then by declaration id in ordinal order, then by file key. A dependency that joins with a lower rank
  than the members waiting behind the targets goes before them.
- **The rounds stay.** As before, the scope is analysed again when the answers applied to its targets differ from those its
  last analysis read. The next round answers what that analysis reaches. A scope's rounds now follow its analyses, not
  the depth of the dependency chains.
- **One lock.** Taking the next member, queuing a member, publishing an answer, a generation's read of the run's answers
  and the time spent all hold one lock, so that several generations can share one queue. A member being generated is not
  queued again. When its answer is published, it rejoins the queue if a dependency it saw changed in the meantime.

## Why the answers stay the same

`SameView` already assumes that a generation's answer depends only on what it saw of its dependencies. Under that
assumption, the queue writes the answers the rounds write:

- Finality, the views and the rule "generate again when a view changes" are unchanged, so every member still ends
  generated against final views.
- Behind the targets, on one thread, no generation reads a model that is generated again later, as long as each reader
  ranks above the dependencies it reads: a dependency's repeat has the dependency's rank, so it runs before any reader
  still waiting.
- Where that does not hold — while the targets go first, between members of one rank, or with several threads — a member
  can read a model that is generated again later, as in a round today. The reader is then generated again if the bytes
  differ.
- A `timeout` is generated once, as before, and keeps the same entry whatever it saw.

## Measured

One serial recording of the demo (`build/record-demo-models.ps1`) on `8b0daa28`, with the queue in order 1 below (rank
first) behind a scratch switch and the per-member trace on. It is compared with #373's recording of the same engine in
round order: generator `cd00a7b8`, another day, and 8.5 GB available against 3.5 GB here.

| | Rounds (#373) | Queue, rank first |
|---|---:|---:|
| Generations | 687 | 654 |
| Repeats | 55 | 22 (136 s) |
| Generation | 2,822 s | 2,579 s |
| Recording wall | 2,891 s | 2,652 s |
| Peak working set | 5,743 MB | 5,498 MB |
| Timeouts | 12 | 13 |
| Answers equal to the committed files | — | 631 of 632 |

- **The saving.** 243 s against #373's recording. The trace prices the rounds' 55 repeats at 451 s with this run's member
  times, so the saving on one day's times is about 315 s. A replay of the rounds over this trace gives 2,894 s, 687
  generations and 55 repeats.
- **The one different answer.** `JsonSerializerOptions.get_Converters` is `timeout` here and `incomplete` in the committed
  file. Its four dependencies were refusals in both runs, so its views were the same. Its time sits on the 120 s member
  limit: #373 measured it at 144 s alone and just under 120 s in its recording, and here it took 120.0 s of wall and
  117.1 s of CPU. The rounds generated it next to the other `System.Text.Json` members. The queue generated it about
  1,200 s later, when a high-rank reader first named it.
- **No answer of a repeat changed.** All 22 repeats wrote the hash of the answer they replaced: 20 refusals and 2 models.
  All 22 are members the rounds also repeated, with the same kind of trigger: a dependency read with no answer later got
  a model. That covers 21 of the rounds' 46 refusal repeats, among them `JsonSerializer.Deserialize<T>(Stream,
  JsonTypeInfo<T>)` and `KestrelServerOptions.Configure`. The rounds' other 25 refusal repeats, the `RoutePattern`
  members and `Build` among them, and 7 of their 9 model repeats do not happen under the queue. Those members are
  generated once, against final views, so whether their first answer under the rounds differed is not observed.
- **The rank costs 3.2 s** for 654 lookups: one resolution of each assembly, then a walk of its references. The 94 files'
  assemblies take ranks from 1 to 25.

## The order for hunts

Replayed over this trace with the rounds' rule and each order, using its member times (the replay of order 1 gives the
recording's own 654 generations, 22 repeats and 2,579 s):

| Order of the queue | Recording, generation | Repeats | Hunt at 900 s: scope targets answered (final) | Handed-delegate members answered |
|---|---:|---:|---:|---:|
| Rounds (ADR 0027) | 2,894 s | 55 | 79 of 80 (69) | 11 of 11 |
| 1. Rank, then handed delegate first | 2,579 s | 22 | 69 of 80 (68) | 11 of 11 |
| 2. Handed delegate first, then rank | 2,585 s | 23 | 69 of 80 (68) | 11 of 11 |
| **3. The last analysis's targets first, handed delegate first among them; then the rest by rank** | **2,598 s** | **27** | **79 of 80 (71)** | **11 of 11** |

The owner chose order 3.

- **Orders 1 and 2 answer fewer scope targets under the run limit.** They generate each low-rank target's dependency
  chain before any higher-rank target, including five `System.Text.Json` timeouts of 120 s each. The 11 targets left
  unanswered at 900 s are of rank 19 to 25: `WebApplication.CreateBuilder`, `Run` and `Services`,
  `WebApplicationBuilder.Build`, `MapControllers`, `AddControllers`, `AddGrpc` and `ControllerBase`. At 300 s they answer
  25 and 26 targets, against the rounds' 79.
- **Order 3 keeps the rounds' breadth.** A target's first generation comes before every dependency and repeat, in the
  order TD-034c gives targets. What its generations read joins the queue by rank behind them. At every limit replayed it
  answers as many targets as the rounds, 79 of 80. Up to the run limit, as many or more of them are final: 69 at 120 s
  and 300 s, 70 at 600 s, 71 at 900 s, against the rounds' 69. It keeps 296 s of the recording's saving, 19 s less than
  order 1.
- **What order 3 gives up.** While the targets go first, a target can read the model of another target that is
  generated again later, as in a round today.
- **Order 3 is replayed, not recorded.** The next recording of the demo measures it.

## Consequences

- The recording generates about 300 s less on the demo, with the same files apart from a member whose time sits on the
  member limit.
- A scope's rounds follow its analyses: on the demo, `Demo.Web` makes two `Generate` calls instead of six, and its
  analyses stay two.
- Tests that pinned a round's composition change with it: which round generated a dependency, how many analyses a repeat
  took.
- CONTEXT's **Generation round** and **Model generator**, and TD-034c's rounds and order sentence, describe the queue.
- Issue #245's workers can share this queue. Its draft makes a round's answers visible only from the next round, which
  would keep about 80 s of this saving. That rule mostly falls away once workers take from one queue.

## Rejected

- **An order inside the round, rounds kept.** It saves about 15 s, because a dependency is still generated only in the next
  round.
- **A dependency generated right after its reader, without a rank** (a work list). Same saving on #244's times, but a
  generation can read a model that is generated again later.
- **Rank first, for hunts as for the recording (orders 1 and 2).** Under the run limit they leave scope targets unanswered
  that the rounds answer.
- **Aborting a generation at its first read of a dependency without an answer, and generating it after the dependency.**
  A member's dependencies are known only by generating it. With this trace's read times, on top of order 1, it would
  save 125.8 s of the 22 repeated first generations. It would spend 30.6 s on the partial generations of 44 members that
  are final at once: about 95 s net. It needs a generator that stops on a read and a rule for the partial attempt's share
  of the member limit. That is a decision of its own.
- **Rank waves with a round's answers visible only from the next round** (#245's draft). It keeps about 80 s of the saving.
