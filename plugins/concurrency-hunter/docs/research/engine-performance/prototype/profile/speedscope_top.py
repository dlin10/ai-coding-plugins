"""Top methods by inclusive and exclusive time on one thread of a dotnet-trace Speedscope export.

usage: speedscope_top.py <file.speedscope.json> [thread-substring] [--top N] [--under FRAME-SUBSTRING] [--callers FRAME-SUBSTRING]

Without a thread substring it picks the thread whose stacks most often contain "WholeProgram" or "ExecutionModel".
--under keeps only the time whose stack contains a frame matching the substring.
--callers prints, for the frames matching the substring, the immediate callers weighted by time.
"""
import json
import sys
from collections import Counter, defaultdict

args = sys.argv[1:]
top = 20
under = None
callers_of = None
positional = []
i = 0
while i < len(args):
    if args[i] == '--top':
        top = int(args[i + 1]); i += 2
    elif args[i] == '--under':
        under = args[i + 1]; i += 2
    elif args[i] == '--callers':
        callers_of = args[i + 1]; i += 2
    else:
        positional.append(args[i]); i += 1

data = json.load(open(positional[0], encoding='utf-8'))
frames = [f['name'] for f in data['shared']['frames']]


def stacks(profile):
    """Yields (stack as a tuple of frame indices, root first; weight)."""
    if profile['type'] == 'sampled':
        for sample, weight in zip(profile['samples'], profile['weights']):
            yield tuple(sample), weight
        return
    stack = []
    last = profile['startValue']
    for event in profile['events']:
        if stack and event['at'] > last:
            yield tuple(stack), event['at'] - last
        last = event['at']
        if event['type'] == 'O':
            stack.append(event['frame'])
        else:
            # close the frame (normally the top)
            for k in range(len(stack) - 1, -1, -1):
                if stack[k] == event['frame']:
                    del stack[k:]
                    break


def matches(stack, needle):
    return any(needle in frames[f] for f in stack)


profiles = data['profiles']
summary = []
for profile in profiles:
    total = 0.0
    engine = 0.0
    for stack, weight in stacks(profile):
        total += weight
        if matches(stack, 'WholeProgram') or matches(stack, 'ExecutionModel'):
            engine += weight
    summary.append((engine, total, profile['name']))

print('threads (engine-time, total, name), unit', profiles[0].get('unit'))
for engine, total, name in sorted(summary, reverse=True)[:8]:
    print(f'  {engine:12.1f} {total:12.1f}  {name}')

if positional[1:]:
    chosen = next(p for p in profiles if positional[1] in p['name'])
else:
    chosen = profiles[max(range(len(profiles)), key=lambda k: summary[k][0])]

inclusive = Counter()
exclusive = Counter()
callers = defaultdict(Counter)
total = 0.0
for stack, weight in stacks(chosen):
    if under and not matches(stack, under):
        continue
    total += weight
    # dotnet-trace puts a CPU_TIME / UNMANAGED_CODE_TIME pseudo-frame at the leaf; exclusive time goes to the nearest real frame
    leaf = len(stack) - 1
    while leaf > 0 and frames[stack[leaf]].isupper():
        leaf -= 1
    exclusive[stack[leaf]] += weight
    for f in set(stack):
        inclusive[f] += weight
    if callers_of:
        for k, f in enumerate(stack):
            if callers_of in frames[f] and k > 0 and (k == 0 or callers_of not in frames[stack[k - 1]]):
                callers[f][stack[k - 1]] += weight


def short(name, width=150):
    return name if len(name) <= width else name[:width - 3] + '...'


print(f'\nthread {chosen["name"]}: {total:.1f} {chosen.get("unit")} sampled' + (f' under "{under}"' if under else ''))
print(f'\ntop {top} by inclusive time')
for f, w in inclusive.most_common(top):
    print(f'  {100 * w / total:6.2f}%  {short(frames[f])}')
print(f'\ntop {top} by exclusive time')
for f, w in exclusive.most_common(top):
    print(f'  {100 * w / total:6.2f}%  {short(frames[f])}')
if callers_of:
    for f, counter in callers.items():
        print(f'\ncallers of {short(frames[f])}')
        for c, w in counter.most_common(12):
            print(f'  {100 * w / total:6.2f}%  {short(frames[c])}')
