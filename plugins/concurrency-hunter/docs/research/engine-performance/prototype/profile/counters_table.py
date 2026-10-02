"""A dotnet-counters CSV (System.Runtime meter) as one row per timestamp: memory, GC counts (cumulative), allocation, pause, CPU.

usage: counters_table.py <counters.csv> [every-nth-row]
"""
import csv
import sys
from collections import OrderedDict

every = int(sys.argv[2]) if len(sys.argv) > 2 else 1
rows = OrderedDict()
for record in csv.DictReader(open(sys.argv[1], encoding='utf-8')):
    rows.setdefault(record['Timestamp'], {})[record['Counter Name']] = float(record['Mean/Increment'])


def pick(values, prefix, generation=None):
    for name, value in values.items():
        if name.startswith(prefix) and (generation is None or f'generation={generation}]' in name):
            return value
    return 0.0


MB = 1048576
print(f'{"time":8} {"ws MB":>7} {"commit":>7} {"gen0":>6} {"gen1":>6} {"gen2 MB":>8} {"loh":>6} {"#gc0":>6} {"#gc1":>5} {"#gc2":>5} '
      f'{"alloc MB/s":>10} {"pause %":>7} {"cpu cores":>9}')
totals = {'gen0': 0, 'gen1': 0, 'gen2': 0}
interval = None
for k, (stamp, values) in enumerate(rows.items()):
    rate_name = next((n for n in values if n.startswith('dotnet.gc.heap.total_allocated')), '')
    if interval is None and '/ ' in rate_name:
        interval = float(rate_name.split('/ ')[1].split(' ')[0])
    for g in totals:
        totals[g] += pick(values, 'dotnet.gc.collections', g)
    if k % every:
        continue
    seconds = interval or 1
    cpu = sum(v for n, v in values.items() if n.startswith('dotnet.process.cpu.time'))
    print(f'{stamp.split(" ")[1]:8} {pick(values, "dotnet.process.memory.working_set") / MB:7.0f} '
          f'{pick(values, "dotnet.gc.last_collection.memory.committed_size") / MB:7.0f} '
          f'{pick(values, "dotnet.gc.last_collection.heap.size", "gen0") / MB:6.0f} '
          f'{pick(values, "dotnet.gc.last_collection.heap.size", "gen1") / MB:6.0f} '
          f'{pick(values, "dotnet.gc.last_collection.heap.size", "gen2") / MB:8.0f} '
          f'{pick(values, "dotnet.gc.last_collection.heap.size", "loh") / MB:6.0f} '
          f'{totals["gen0"]:6.0f} {totals["gen1"]:5.0f} {totals["gen2"]:5.0f} '
          f'{pick(values, "dotnet.gc.heap.total_allocated") / MB / seconds:10.0f} '
          f'{100 * pick(values, "dotnet.gc.pause.time") / seconds:7.1f} {cpu / seconds:9.2f}')
