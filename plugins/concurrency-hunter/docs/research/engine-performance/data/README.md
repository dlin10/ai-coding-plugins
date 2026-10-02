# Raw results

Output of the runs of 2026-10-01 behind `../../engine-performance.md`. The traces (`.nettrace`, `.speedscope.json`) and
gcdumps (120–180 MB each) are not kept; these files are what was read from them.

| File | Run |
|---|---|
| `solve-four.log`, `solve-four-timeline.txt` | Harness output and script timeline of the `four` solve (stopped by its 10-minute cap) |
| `solve-four-top45.txt` | Top 45 methods by inclusive and exclusive time on the solver thread, 180 s sampled |
| `solve-four-counters.txt` | Counters every 5 s over the solve: working set, generation sizes, cumulative GC counts, allocation rate, pause, CPU |
| `solve-four-1.retained.txt`, `solve-four-2.retained.txt` | `gcretained` on the gcdumps at minute 3:11 and 8:07 of the solve |
| `exec-all.log`, `exec-all-timeline.txt` | Harness output and timeline of the `all` run (executions stopped by the machine guard at 15.9 GB) |
| `exec-all-counters.txt`, `exec-all-ws.csv` | Counters every second; working set, private bytes and free commit every 200 ms over the executions stage |
| `exec-all-stacks-1.txt`, `exec-all-stacks-2.txt` | `dotnet-stack` snapshots at 2 GB and 4 GB |
| `exec-all-1.retained.txt`, `exec-all-2.retained.txt` | `gcretained` on the gcdumps at 2 GB and 4 GB, with the types matching `Execution` |
