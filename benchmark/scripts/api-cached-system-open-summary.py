# Summarizes one api-cached-system-open-20261028.sh run: the quiet gate per arm, every paired delta and the predeclared verdicts.
# Gate (as in the CORS runs): within each arm's window, external CPU mean <= 25 %, p95 <= 100 % (of one core), and no external
# build/compile process at >= 10 % in any sample. A failing arm makes the whole run invalid; nothing is dropped or filtered.
# Predeclared allocation question (committed before the run): does B/op fall consistently, as the profile predicted (~356 B/op)?
#   A cell falls consistently when after < before in >= 5 of 6 pairs and the median paired delta is negative.
#   REPRODUCED:     both c16 cells (small, medium) fall consistently.
#   NOT REPRODUCED: neither c16 cell has more than 3 of 6 pairs lower with a negative median.
#   UNRESOLVED:     anything else. The c1 cells are reported beside it, not pooled into it.
# Predeclared worsening flag: in any cell, any metric worse in >= 5 of 6 pairs with a worse median is a CONCERN. Its absence
# is not parity: this run has no non-inferiority tolerance and makes no latency claim.
# Predeclared slow-state flag: a small/c1 arm whose B/op exceeds the median of all small/c1 arms by >= 2,000 or whose CPU/op
# exceeds it by >= 25 µs is flagged. Flagged arms stay in every table; no filtered aggregate replaces the full one.
import datetime, json, re, statistics, sys
from pathlib import Path

out, pairs = Path(sys.argv[1]), int(sys.argv[2])
builder = re.compile(r'^(dotnet|MSBuild|VBCSCompiler|csc|cc1|cc1plus|ld|lld|rustc|cargo|go|gcc|clang|make|ninja|nix|nix-daemon|node)$')
def ts(text): return datetime.datetime.fromisoformat(text.strip().rstrip('Z')[:26]).replace(tzinfo=datetime.timezone.utc)
order = [(arm, r) for r in range(1, pairs + 1) for arm in (('before', 'after') if r % 2 else ('after', 'before'))]
cells = {}
for arm, r in order:
    for line in (out / f'{arm}-{r}.md').read_text().splitlines():
        if line.startswith('{'):
            c = json.loads(line)
            cells[(c['Size'], c['Concurrency'], arm, r)] = c

samples = [json.loads(line) for line in (out / 'cpu-processes.jsonl').read_text().splitlines() if line.strip()]
for sample in samples: sample['t'] = datetime.datetime.fromisoformat(sample['utc'])
valid = True
print('## Quiet gate (arms in run order)\n')
print('| Arm | samples | external mean % | external p95 % | max external builder % | verdict |')
print('| --- | ---: | ---: | ---: | ---: | --- |')
for arm, r in order:
    start, end = (ts(line) for line in (out / f'{arm}-{r}.window').read_text().split())
    inside = [s for s in samples if start <= s['t'] <= end]
    external = sorted(s['external_cpu'] for s in inside)
    mean = statistics.fmean(external) if external else float('nan')
    p95 = external[min(len(external) - 1, int(0.95 * len(external)))] if external else float('nan')
    build = max([cpu for s in inside for cpu, _, name in s['external_top'] if builder.match(name)], default=0.0)
    ok = bool(external) and mean <= 25 and p95 <= 100 and build < 10
    valid &= ok
    print(f'| {arm}-{r} | {len(external)} | {mean:.1f} | {p95:.1f} | {build:.1f} | {"pass" if ok else "FAIL"} |')
print(f'\nRun gate: {"PASS" if valid else "FAIL: this run is invalid"}\n')

def value(cell, key): return cell[key] if key in cell else cell['Server'][key]
metrics = [('AllocatedBytesPerOp', 'B/op', 0, 1), ('CpuMicrosPerOp', 'CPU µs/op', 1, 1), ('OpsPerSecond', 'ops/s', 0, -1),
           ('P50Micros', 'client p50 µs', 1, 1), ('P99Micros', 'client p99 µs', 1, 1), ('ServerP50Micros', 'server p50 µs', 1, 1),
           ('ServerP99Micros', 'server p99 µs', 1, 1), ('EngineP50Micros', 'engine p50 µs', 1, 1), ('EngineP99Micros', 'engine p99 µs', 1, 1),
           ('OverheadP50Micros', 'overhead p50 µs', 1, 1), ('OverheadP99Micros', 'overhead p99 µs', 1, 1)]
grid = [(size, conc) for size in ('small', 'medium') for conc in (1, 16)]
falls, concerns = {}, []
print('## Paired Δ = after − before within a pair (worse = higher, except ops/s)\n')
print('| Size | Conc | Metric | before range | after range | before median | after median | paired Δ p1 … p6 | worse pairs | median Δ |')
print('| --- | ---: | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: |')
for size, conc in grid:
    if any((size, conc, arm, r) not in cells for arm, r in order):
        print(f'| {size} | {conc} | MISSING | | | | | | | |'); valid = False; continue
    for key, label, digits, sign in metrics:
        b = [value(cells[(size, conc, 'before', r)], key) for r in range(1, pairs + 1)]
        a = [value(cells[(size, conc, 'after', r)], key) for r in range(1, pairs + 1)]
        d = [x - y for x, y in zip(a, b)]
        worse, median = sum(sign * x > 0 for x in d), statistics.median(d)
        if key == 'AllocatedBytesPerOp':
            falls[(size, conc)] = (sum(x < 0 for x in d), median)
        if worse >= pairs - 1 and sign * median > 0:
            concerns.append(f'{size}/c{conc} {label}: {worse}/{pairs} worse, median Δ {median:+,.{digits}f}')
        f = lambda v: f'{v:,.{digits}f}'
        print(f'| {size} | {conc} | {label} | {f(min(b))}–{f(max(b))} | {f(min(a))}–{f(max(a))} | {f(statistics.median(b))} | '
              f'{f(statistics.median(a))} | {", ".join(f(x) for x in d)} | {worse}/{pairs} | {f(median)} |')
errors = sum(c['Errors'] for c in cells.values())
print(f'\nErrors across all arms: {errors}.\n')

print('## small/c1 slow-state flag\n')
if all(('small', 1, arm, r) in cells for arm, r in order):
    small = {(arm, r): cells[('small', 1, arm, r)] for arm, r in order}
    bytes_median = statistics.median(c['AllocatedBytesPerOp'] for c in small.values())
    cpu_median = statistics.median(c['CpuMicrosPerOp'] for c in small.values())
    flagged = [f'{arm}-{r} (B/op {c["AllocatedBytesPerOp"] - bytes_median:+,.0f}, CPU/op {c["CpuMicrosPerOp"] - cpu_median:+.1f} µs, '
               f'ops/s {c["OpsPerSecond"]:,.0f})' for (arm, r), c in small.items()
               if c['AllocatedBytesPerOp'] - bytes_median >= 2000 or c['CpuMicrosPerOp'] - cpu_median >= 25]
    print(f'Medians over all {len(small)} small/c1 arms: B/op {bytes_median:,.0f}, CPU/op {cpu_median:.1f} µs.')
    print('Flagged: ' + ('; '.join(flagged) if flagged else 'none') + '\n')

print('## Predeclared verdicts\n')
for (size, conc), (lower, median) in falls.items():
    print(f'- B/op {size}/c{conc}: lower in {lower}/{pairs} pairs, median Δ {median:+,.0f} (profile prediction about −356)')
if not valid or errors or len(falls) != len(grid):
    verdict = 'INVALID (gate, missing cell or errors); no assessment'
else:
    c16 = [falls[(size, 16)] for size in ('small', 'medium')]
    if all(lower >= pairs - 1 and median < 0 for lower, median in c16):
        verdict = 'REPRODUCED'
    elif all(lower <= pairs // 2 or median >= 0 for lower, median in c16):
        verdict = 'NOT REPRODUCED'
    else:
        verdict = 'UNRESOLVED'
print(f'\nAllocation verdict (c16 cells): {verdict}')
print('\nWorsening flag: ' + ('CONCERN: ' + '; '.join(concerns) if concerns else
      'no metric met the rule; this is not a parity or non-inferiority result'))
