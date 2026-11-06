# The recording generates four members at once, and the member limit counts the member's CPU time

Status: Accepted

This decision amends how [ADR 0031](0031-the-recording-of-committed-generated-models-has-a-run-limit-of-its-own.md)'s
recording spends its time, and how SPEC TD-034b and TD-034c measure the member limit. The queue, its order, finality,
the views and the repeat rule stay as [ADR 0032](0032-a-round-generates-from-one-queue-by-assembly-rank-and-a-dependency-read-without-an-answer-joins-it-at-once.md)
has them.

## Context

The demo's recording spends about 2,600 s inside generations, one member at a time, on a machine with 16 logical CPUs.
Running members at once is the largest lever left on that time. Three things decide whether it is safe:

- **What a generation reads.** ADR 0032's queue already lets several threads take, queue and publish under one lock.
  A member whose view of a dependency changed while it was generated rejoins the queue. Workers therefore change how
  many repeats a run makes, not the final answers, as long as a generation's answer depends only on its views.
- **The member limit is wall time.** With members at once, a member near 120 s can be cut by its neighbours' load.
  Measured before the queue, on `68a34de8` under workstation GC: two members at once slowed members 1.37 times
  (median), and a partial run with four slowed them 1.79 times and turned `RoutePattern.get_RawText` into `timeout`.
- **Memory.** Each member in flight holds its own analysis. The System.Text.Json members peak at 1.6–3.0 GB each
  alone, and sit next to one another in the queue.

## Decision

- **The recording generates four members at once.**
  - Under `GenerationBudget.Recording`, a round's queue is drained by four worker threads. Each takes the first member,
    generates it and publishes its answer, as ADR 0032 has one thread do.
  - A worker that finds the queue empty waits while another worker has a member in flight, because publishing that
    member's answer can queue more. The round ends when the queue is empty and no member is in flight.
  - Four is a constant of the server, like the limits. It does not follow the machine's core count.
  - The recording limit keeps counting time spent inside generations, summed over members: the budget's measure and the
    run's `generation` timing. With four at once that sum exceeds the generation step's wall time. Two hours still leave
    more than twice the demo's 2,800 s.
- **Every other run generates one member at a time**, and spends the run limit in the queue's order as TD-034c says.
- **The member limit is the CPU time of the member's own thread.**
  - The 120 s member limit counts the CPU time of the thread that generates the member, across both attempts.
  - Library decompilation and compilation on pool threads, recipe opening, collections on GC threads and the wait for a
    compilation another member started are not counted.
  - A wall-clock backstop of four member limits, 480 s, ends a member that waits without spending CPU. A cut by either is
    `timeout`.
  - The same limit applies wherever the member limit applies: a run, `generate` and the model evals. The CoreLib tool
    keeps its own limit.
  - In a run, a member whose own limit is not larger than what is left of the run's limit runs under its own limit
    alone, as before. Its wall time can then exceed what was left by as long as it waited without spending CPU time. A
    member whose own limit is larger runs under what is left of the run's limit, on the clock, and a cut is `budget`.
- **The executable runs with server GC.** Under workstation GC a blocking collection runs on the thread that triggered
  it and also collects the other members' garbage, so a member's CPU time would still move with its neighbours.

## Measured

Three recordings of the demo (`build/record-demo-models.ps1`) on `0992faca`, each alone on the machine: 8 physical and 16
logical CPUs, 32 GB. The workers and the CPU limit were behind scratch switches, with the per-member trace on. The serial
run is ADR 0032's recording of the queue under workstation GC and the wall limit.

| | One at a time | Two at once | **Four at once** |
|---|---:|---:|---:|
| Generation step, wall | 2,580 s | 1,304 s | **734 s** |
| Recording, wall | 2,652 s | 1,373 s | **802 s** |
| Time inside generations, summed | 2,579 s | 2,604 s | 2,773 s |
| Generations / repeats | 654 / 22 | 660 / 28 | 663 / 31 |
| Peak working set | 5,498 MiB | 7,728 MiB | **11,602 MiB** |
| Timeouts, and their wall time | 13, 120 s | 14, 124–140 s | 14, 128–142 s |
| Answers equal to the committed files | 631 of 632 | 630 of 632 | 630 of 632 |

- **Members are not slower.** Against the serial run, the 14 members of 10 s or more took 0.83 times their wall time
  with four at once (median; p90 0.99) and 0.78 times their CPU time (p90 1.07). The slowest member that is not a
  timeout, `get_RawText`, spent 68.9 s of CPU of the 120. Server GC and the CPU limit together remove the slowdown that
  workstation GC and the wall limit showed. How much of the speed is server GC alone was not measured.
- **Two and four at once wrote the same answers.** Against the serial run, one answer differs in both:
  `JsonSerializerOptions.MakeReadOnly` is `incomplete` serially, at 110.4 s of wall and 108.4 s of CPU, and `timeout`
  with workers, cut at 120 s of CPU. It ran beside `GetTypeInfo` and `get_Options`, the heaviest members, and its CPU
  time grew by more than 11 % while the median member's fell. `JsonSerializerOptions.get_Converters` is `timeout` in
  all three runs and `incomplete` in the committed file, as ADR 0032 found.
- **The peak comes while the System.Text.Json members run together.** With four at once the machine kept 4.0 GB
  available at the peak.
- **The timeouts take longer.** Cut at 120 s of CPU, they end 128–142 s after they start.

## Consequences

- The recording takes about 13 minutes instead of 44 on the demo, and peaks at 11.3 GiB instead of 5.4.
- A member whose CPU time sits near the member limit can come out differently with workers than alone: one member of 632
  on the demo. That is the same kind of change ADR 0032 found for a member's position in the queue. A recording records
  what it finds, and the committed files are those of the four-worker recording.
- `timings.generation` of the recording is the summed member time, not the wall time of its generation step.
- The CPU time is read through `ProcessThread.TotalProcessorTime`. .NET gives a managed thread's system id to no public
  member, so the analyzer imports one native function, `kernel32!GetCurrentThreadId`. SPEC TD-034b and
  `GeneratorBoundaryTests` allow that import and no other. The executable ships for win-x64 only.
- The limit reads the CPU time when a whole limit has passed on the clock, and then again when what is left of it could
  have been spent, at least 50 ms later. A thread spends no more CPU time than passes on the clock, so the cut comes at
  most 50 ms after the limit is spent.
- A timeout still depends on the machine's speed. CPU time removes waiting, collections on GC threads and others'
  compilations from the limit. It does not remove a slower core, contention for memory or a different GC.
- Tests that construct a run keep one worker, so the tests that pin the queue's sequence and the run limit hold. The
  workers get tests of their own: the wait while another member is in flight, a failure in one worker ending all of them,
  and the answers equal to one worker's.
- CONTEXT's **Generation budget** and **Generation round**, and TD-034b's and TD-034c's limit sentences, describe the CPU
  limit and the recording's workers.

## Not decided here

- **Workers in a hunt.** With workers, the run limit is overspent by up to three members, because a member's time is
  added when it ends. Which members a hunt answers would then vary from run to run on one machine. A hunt also runs on
  the user's machine, beside the user's programs, and eShop was not measured. [ADR 0029](0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md)
  takes generation out of the hunt.
- **A limit counted in the engine's own work.** A count of the generator's checks would make a timeout independent of
  the machine, which neither wall nor CPU time does. It needs a calibration against 120 s, and the checks do not cost
  the same.

## Rejected

- **Two members at once.** It peaks at 7.5 GiB instead of 11.3 and writes the same answers, but the recording takes 23
  minutes instead of 13. The owner chose four.
- **A round's answers visible only from the next round** (an earlier draft of this decision). Under ADR 0032's queue it
  would keep about 80 s of the queue's 300 s saving, and the queue's repeat rule already keeps the answers.
- **Keeping the wall limit with members at once.** The limit is where load turns into a different answer: under
  workstation GC and the wall limit, four at once turned `get_RawText` into `timeout`.
- **Generating each timeout again alone to confirm it.** The demo has 14 timeouts. Confirming them would add about
  1,700 s of one-at-a-time generation, more than the workers save.
