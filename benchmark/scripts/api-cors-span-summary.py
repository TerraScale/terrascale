# Summarizes one api-cors-span-20261028.sh run: the predeclared quiet gate per arm, then every cell of every arm.
# Gate (declared before the run): within each arm's window, external CPU mean <= 25 %, p95 <= 100 % (of one core),
# and no external build/compile process at >= 10 % in any sample. A failing arm makes the whole run invalid; nothing is dropped.
import datetime, json, re, statistics, sys
from pathlib import Path

out = Path(sys.argv[1])
builder = re.compile(r'^(dotnet|MSBuild|VBCSCompiler|csc|cc1|cc1plus|ld|lld|rustc|cargo|go|gcc|clang|make|ninja|nix|nix-daemon|node)$')
def ts(text): return datetime.datetime.fromisoformat(text.strip().rstrip('Z')[:26]).replace(tzinfo=datetime.timezone.utc)
samples = [json.loads(line) for line in (out / 'cpu-processes.jsonl').read_text().splitlines() if line.strip()]
for sample in samples: sample['t'] = datetime.datetime.fromisoformat(sample['utc'])
arms, valid = [], True
print('## Quiet gate\n')
print('| Arm | samples | external mean % | external p95 % | max external builder % | verdict |')
print('| --- | ---: | ---: | ---: | ---: | --- |')
for repeat in (1, 2, 3):
    for arm in (('before', 'after') if repeat % 2 else ('after', 'before')):
        start, end = (ts(line) for line in (out / f'{arm}-{repeat}.window').read_text().split())
        inside = [s for s in samples if start <= s['t'] <= end]
        external = sorted(s['external_cpu'] for s in inside)
        mean = statistics.fmean(external) if external else float('nan')
        p95 = external[min(len(external) - 1, int(0.95 * len(external)))] if external else float('nan')
        build = max([cpu for s in inside for cpu, _, name in s['external_top'] if builder.match(name)], default=0.0)
        ok = bool(external) and mean <= 25 and p95 <= 100 and build < 10
        valid &= ok
        print(f'| {arm}-{repeat} | {len(external)} | {mean:.1f} | {p95:.1f} | {build:.1f} | {"pass" if ok else "FAIL"} |')
        for line in (out / f'{arm}-{repeat}.md').read_text().splitlines():
            if line.startswith('{'):
                arms.append(dict(arm=arm, repeat=repeat, **json.loads(line)))
print(f'\nRun gate: {"PASS" if valid else "FAIL: this run is invalid"}\n')

def value(cell, key):
    server = cell.get('Server') or {}
    return cell[key] if key in cell else server[key]
metrics = [('AllocatedBytesPerOp', 'B/op', 0), ('CpuMicrosPerOp', 'CPU µs/op', 1), ('OpsPerSecond', 'ops/s', 0),
           ('P50Micros', 'client p50 µs', 1), ('P99Micros', 'client p99 µs', 1), ('OverheadP50Micros', 'overhead p50 µs', 1),
           ('OverheadP99Micros', 'overhead p99 µs', 1), ('EngineP50Micros', 'engine p50 µs', 1), ('EngineP99Micros', 'engine p99 µs', 1),
           ('ServerP50Micros', 'server p50 µs', 1)]
print('## Cells (api, read; ranges are min–max of 3 repeats; paired Δ = after − before within a repeat)\n')
print('| Size | Conc | Metric | before range | after range | paired Δ (r1, r2, r3) | median paired Δ |')
print('| --- | ---: | --- | ---: | ---: | ---: | ---: |')
for size in ('small', 'medium'):
    for conc in (1, 16):
        cells = {(c['arm'], c['repeat']): c for c in arms if c['Size'] == size and c['Concurrency'] == conc}
        if len(cells) != 6:
            print(f'| {size} | {conc} | MISSING ({len(cells)} of 6 arms) | | | | |'); valid = False; continue
        for key, name, digits in metrics:
            b = [value(cells[('before', r)], key) for r in (1, 2, 3)]
            a = [value(cells[('after', r)], key) for r in (1, 2, 3)]
            d = [x - y for x, y in zip(a, b)]
            f = lambda v: f'{v:,.{digits}f}'
            print(f'| {size} | {conc} | {name} | {f(min(b))}–{f(max(b))} | {f(min(a))}–{f(max(a))} | {", ".join(f(x) for x in d)} | {f(statistics.median(d))} |')
errors = sum(c['Errors'] for c in arms)
print(f'\nErrors across all arms: {errors}. Overall: {"VALID" if valid and errors == 0 else "INVALID"}')
