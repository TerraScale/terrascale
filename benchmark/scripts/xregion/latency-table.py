# Usage: agg.py <dir>; prints a markdown table per direction: floor, pooled p50/p95/p99, overhead p50, per-repeat p50 spread.
import sys, json, glob, os, statistics as st
d = sys.argv[1]
dirs = {}
for f in sorted(glob.glob(os.path.join(d, '*-r*.jsonl'))):
    label = os.path.basename(f).rsplit('-r', 1)[0]
    rows, summ = [], None
    for l in open(f):
        s = json.loads(l)
        if 'summary' in s: summ = s
        else: rows.append(s)
    if summ: dirs.setdefault(label, []).append((summ, rows))
def pct(v, q):
    v = sorted(v); return v[min(len(v) - 1, int(round(q / 100 * (len(v) - 1))))]
print('| Direction | Floor (TCP RTT/2) ms | p50 ms | p95 ms | p99 ms | Overhead p50 ms | p50 per repeat (min-max) | Probes | Missing |')
print('| --- | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: |')
order = ['gru-sjc', 'gru-fra', 'sjc-gru', 'sjc-fra', 'fra-gru', 'fra-sjc']
for label in [o for o in order if o in dirs] + [k for k in dirs if k not in order]:
    runs = dirs[label]
    lat = [r['latency_ms'] for _, rows in runs for r in rows]
    ov = [r['overhead_ms'] for _, rows in runs for r in rows]
    floor = st.median(s['floor_ms'] for s, _ in runs)
    p50s = [s['latency']['p50_ms'] for s, _ in runs]
    miss = sum(s['missing'] for s, _ in runs)
    print(f"| {label.replace('-', ' -> ')} | {floor:.1f} | {pct(lat,50):.1f} | {pct(lat,95):.1f} | {pct(lat,99):.1f} | {pct(ov,50):.1f} | {st.median(p50s):.1f} ({min(p50s):.1f}-{max(p50s):.1f}) | {len(lat)} | {miss} |")
