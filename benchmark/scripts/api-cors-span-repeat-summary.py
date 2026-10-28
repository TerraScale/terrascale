# Summarizes one api-cors-span-repeat-20261028.sh run: the quiet gate per arm, every paired delta, and the predeclared verdict.
# Gate (same as b1): within each arm's window, external CPU mean <= 25 %, p95 <= 100 % (of one core), and no external
# build/compile process at >= 10 % in any sample. A failing arm makes the whole run invalid; nothing is dropped.
# Predeclared assessment (committed before the run), on small/c16 for the four metrics b1 flagged: overhead p99, client p99
# and client p50 (worse = after > before), and ops/s (worse = after < before). For each metric, count the pairs that are worse.
#   REPRODUCED:     at least two metrics have >= 5 of 6 pairs worse, with the median paired delta worse.
#   NOT REPRODUCED: every metric has <= 3 of 6 pairs worse and a median paired delta that is not worse.
#   UNRESOLVED:     anything else. NOT REPRODUCED bounds nothing below this run's noise; it is not a neutrality proof.
# b1's three pairs are listed for comparison only: they raised the hypothesis, so they are not pooled into the verdict.
import datetime, json, re, statistics, sys
from pathlib import Path

out, pairs = Path(sys.argv[1]), int(sys.argv[2])
b1 = Path(sys.argv[3]) if len(sys.argv) > 3 else None
builder = re.compile(r'^(dotnet|MSBuild|VBCSCompiler|csc|cc1|cc1plus|ld|lld|rustc|cargo|go|gcc|clang|make|ninja|nix|nix-daemon|node)$')
def ts(text): return datetime.datetime.fromisoformat(text.strip().rstrip('Z')[:26]).replace(tzinfo=datetime.timezone.utc)
def order(n): return [(arm, r) for r in range(1, n + 1) for arm in (('before', 'after') if r % 2 else ('after', 'before'))]
def load(folder, n):
    cells = {}
    for arm, r in order(n):
        for line in (folder / f'{arm}-{r}.md').read_text().splitlines():
            if line.startswith('{'):
                c = json.loads(line)
                cells[(c['Size'], c['Concurrency'], arm, r)] = c
    return cells

samples = [json.loads(line) for line in (out / 'cpu-processes.jsonl').read_text().splitlines() if line.strip()]
for sample in samples: sample['t'] = datetime.datetime.fromisoformat(sample['utc'])
valid = True
print('## Quiet gate (arms in run order)\n')
print('| Arm | samples | external mean % | external p95 % | max external builder % | verdict |')
print('| --- | ---: | ---: | ---: | ---: | --- |')
for arm, r in order(pairs):
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
metrics = [('OverheadP99Micros', 'overhead p99 µs', 1, 1), ('P99Micros', 'client p99 µs', 1, 1), ('P50Micros', 'client p50 µs', 1, 1),
           ('OpsPerSecond', 'ops/s', 0, -1), ('OverheadP50Micros', 'overhead p50 µs', 1, 1), ('ServerP50Micros', 'server p50 µs', 1, 1),
           ('ServerP99Micros', 'server p99 µs', 1, 1), ('EngineP50Micros', 'engine p50 µs', 1, 1), ('EngineP99Micros', 'engine p99 µs', 1, 1),
           ('CpuMicrosPerOp', 'CPU µs/op', 1, 1), ('AllocatedBytesPerOp', 'B/op', 0, 1)]
runs = [('repeat', out, pairs)] + ([('b1', b1, 3)] if b1 else [])
data = {name: load(folder, n) for name, folder, n in runs}
primary = {}
print('## Paired Δ = after − before within a pair (worse = higher, except ops/s)\n')
print('| Run | Size | Conc | Metric | before range | after range | paired Δ by pair | worse pairs | median Δ |')
print('| --- | --- | ---: | --- | ---: | ---: | --- | ---: | ---: |')
for name, folder, n in runs:
    for size in ('small', 'medium'):
        for conc in (16, 1):
            cells = data[name]
            if any((size, conc, arm, r) not in cells for arm, r in order(n)):
                print(f'| {name} | {size} | {conc} | MISSING | | | | | |'); valid = False; continue
            for key, label, digits, sign in metrics:
                b = [value(cells[(size, conc, 'before', r)], key) for r in range(1, n + 1)]
                a = [value(cells[(size, conc, 'after', r)], key) for r in range(1, n + 1)]
                d = [x - y for x, y in zip(a, b)]
                worse, median = sum(sign * x > 0 for x in d), statistics.median(d)
                if name == 'repeat' and (size, conc) == ('small', 16) and key in ('OverheadP99Micros', 'P99Micros', 'P50Micros', 'OpsPerSecond'):
                    primary[label] = (worse, sign * median > 0)
                f = lambda v: f'{v:,.{digits}f}'
                print(f'| {name} | {size} | {conc} | {label} | {f(min(b))}–{f(max(b))} | {f(min(a))}–{f(max(a))} | '
                      f'{", ".join(f(x) for x in d)} | {worse}/{n} | {f(median)} |')
errors = sum(c['Errors'] for cells in data.values() for c in cells.values())
print(f'\nErrors across all arms: {errors}.')
if not valid or errors or len(primary) != 4:
    verdict = 'INVALID (gate, missing cell or errors); no assessment'
elif sum(w >= pairs - 1 and m for w, m in primary.values()) >= 2:
    verdict = 'REPRODUCED'
elif all(w <= pairs // 2 and not m for w, m in primary.values()):
    verdict = 'NOT REPRODUCED'
else:
    verdict = 'UNRESOLVED'
print('\nPrimary small/c16: ' + ', '.join(f'{k} {w}/{pairs} worse, median {"worse" if m else "not worse"}' for k, (w, m) in primary.items()))
print(f'\nPredeclared verdict: {verdict}')
