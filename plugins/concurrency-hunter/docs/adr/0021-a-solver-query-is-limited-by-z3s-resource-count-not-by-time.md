# A solver query is limited by Z3's resource count, not by time

ADR 0004 made the 150 ms per-query limit of TD-094 Z3's own timeout, and the run's solver budget, a tenth
of the deadline, is measured with a clock. Both make the answer depend on how busy the machine is, and the
answer decides findings: `Unsat` takes a candidate away, while `Unknown` — what a timeout gives — keeps it.
A query Z3 refutes in a few milliseconds on an idle machine can run out of time when several analyses
share the processor, and the same program then reports one finding more. The demo's three-run check saw
exactly that: three concurrent analyses of one program, one fingerprint apart.

The decision: Z3 is stopped by its resource limit (`rlimit`), which counts the solver's own work, and the
run's budget is a sum of the same units. Both are constants of the server, calibrated once on the demo
and on eShop so that no answer either gives today changes, and the budget is sized to what a tenth of the
deadline bought at calibration. The same query then gets the same answer on any machine under any load,
and so does the whole run.

- A wall-clock timeout stays only as a safeguard against a pathological query, set well above what a
  calibrated query takes. A query it stops answers `Unknown`, as any undecided query does, and coverage
  counts it apart from the `Unknown`s Z3 itself gives, so a run that depended on the clock says so.
- The run's deadline and the late response stay wall-clock: they bound the whole run, not the solver's
  answers.
- Each query spends its whole limit of the budget, whatever work it used, so the budget is a number of
  queries: a query the safeguard stops moves no later query, and with the order candidates are asked in
  stable (TD-094) an exhausted budget leaves the same candidates `Unknown` on every run.
- Each query is decided in a Z3 context of its own, made for the query and disposed after it: in a
  context queries share, a query's work depends on what earlier queries left there and on when the
  runtime frees it, and so on the load.

The consequences to hold. A query may take longer than 150 ms on a loaded machine; what bounds it is the
work, and the safeguard. TD-094 and ADR 0004's sentence on the limit state the rule in these terms. A
newer Z3 counts its work differently, so a change of the shipped Z3 is a re-calibration, and every number
it moves is named in the run record.

Rejected: keeping the clock and making the check tolerant — the product would still give a different
report for the same program on a busy machine. Rejected: the resource limit per query with the budget left
on the clock — the large programs the budget exists for are exactly where it runs out, and there the
report would still depend on load.
