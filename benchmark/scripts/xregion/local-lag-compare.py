#!/usr/bin/env python3
"""Markdown A/B tables over interleaved local-lag.sh rounds: <out>/<build>/<mode>-r<round>/ (mode idle or load), as
written by one job that alternates the builds per round. Per direction and build: write-to-visible p50 / p99 (pooled over
rounds, and the per-round median with its min-max), the step split and the remote ack delay from each node's /metrics.

usage: local-lag-compare.py <out-dir> [build ...]
"""

import collections
import glob
import importlib.util
import json
import os
import statistics
import sys

spec = importlib.util.spec_from_file_location('table', os.path.join(os.path.dirname(__file__), 'local-lag-table.py'))
table = importlib.util.module_from_spec(spec)
spec.loader.exec_module(table)
REGIONS = ['gru', 'sjc', 'fra']


def spread(values):
    return f'{statistics.median(values):.0f} ({min(values):.0f}-{max(values):.0f})'


def probes(run):
    """Per direction, the visible latencies of one run; and the load writers' writes/s."""
    rows, load = collections.defaultdict(list), []
    for path in glob.glob(os.path.join(run, 'probe-*.jsonl')):
        _, a, b = os.path.basename(path)[len('probe-'):-len('.jsonl')].split('-')
        for line in open(path):
            sample = json.loads(line)
            if 'summary' not in sample:
                rows[(a, b)].append(sample['visible_ms'])
            elif sample.get('load_writers'):
                load.append(sample['load_writes_per_s'])
    return rows, load


def main(out, builds):
    for mode in ('idle', 'load'):
        runs = {b: sorted(glob.glob(os.path.join(out, b, f'{mode}-r*'))) for b in builds}
        if not any(runs.values()):
            continue
        data = {b: [probes(r) for r in runs[b]] for b in builds}
        print(f'### {mode}: write at A → readable at B, ms (pooled p50 / p99; per round p50 and p99 as median (min-max))\n')
        print('| Direction | ' + ' | '.join(f'{b} p50 | p99 | rounds p50 | rounds p99' for b in builds) + ' |')
        print('| --- |' + ' ---: | ---: | --- | --- |' * len(builds))
        for a in REGIONS:
            for c in REGIONS:
                if a == c:
                    continue
                cells = []
                for b in builds:
                    per = [rows[(a, c)] for rows, _ in data[b] if rows.get((a, c))]
                    pooled = [v for p in per for v in p]
                    cells.append(f'{table.pct(pooled, 50):.0f} | {table.pct(pooled, 99):.0f} | {spread([table.pct(p, 50) for p in per])} '
                                 f'| {spread([table.pct(p, 99) for p in per])}' if pooled else '- | - | - | -')
                print(f'| {a} → {c} | ' + ' | '.join(cells) + ' |')
        cells = []
        for b in builds:
            pooled = [v for rows, _ in data[b] for p in rows.values() for v in p]
            cells.append(f'{table.pct(pooled, 50):.0f} | {table.pct(pooled, 99):.0f} | | ' if pooled else '- | - | | ')
        print('| all | ' + ' | '.join(cells) + ' |')
        if mode == 'load':
            print('\nWrites/s the 8 origin writers reached (median (min-max) over probe runs): '
                  + ', '.join(f'{b} {spread([w for _, load in data[b] for w in load])}' for b in builds if any(load for _, load in data[b])))

        print(f'\nSteps ({mode}; `terrabase_replication_step_duration_seconds`; median over node-runs of mean / p50 / p99, ms):\n')
        print('| Step | ' + ' | '.join(builds) + ' |')
        print('| --- |' + ' ---: |' * len(builds))
        steps = {b: collections.defaultdict(list) for b in builds}
        acks = {b: collections.defaultdict(list) for b in builds}
        for b in builds:
            for run in runs[b]:
                for region in REGIONS:
                    try:
                        series = table.delta(run, region)
                    except FileNotFoundError:
                        continue
                    for step, (count, total, buckets) in table.histograms(series, 'terrabase_replication_step_duration_seconds', 'step').items():
                        if count:
                            steps[b][step].append((1000 * total / count, 1000 * table.quantile(buckets, count, .5), 1000 * table.quantile(buckets, count, .99)))
                    for peer, (count, total, buckets) in table.histograms(series, 'terrabase_replication_remote_ack_delay_seconds', 'peer_region').items():
                        if count:
                            acks[b][f'{region} → {peer}'].append((1000 * table.quantile(buckets, count, .5), 1000 * table.quantile(buckets, count, .99)))
        for step in sorted({s for b in builds for s in steps[b]}):
            cells = []
            for b in builds:
                v = steps[b].get(step)
                cells.append(' / '.join(f'{statistics.median(x[i] for x in v):.1f}' for i in range(3)) if v else '-')
            print(f'| {step} | ' + ' | '.join(cells) + ' |')
        print(f'\nRemote ack delay ({mode}; origin stamp → peer region durable ack; median over rounds of p50 / p99, ms):\n')
        print('| Direction | ' + ' | '.join(builds) + ' |')
        print('| --- |' + ' ---: |' * len(builds))
        for direction in sorted({d for b in builds for d in acks[b]}):
            cells = []
            for b in builds:
                v = acks[b].get(direction)
                cells.append(f'{statistics.median(x[0] for x in v):.0f} / {statistics.median(x[1] for x in v):.0f}' if v else '-')
            print(f'| {direction} | ' + ' | '.join(cells) + ' |')
        print()


if __name__ == '__main__':
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2:] or sorted(d for d in os.listdir(sys.argv[1]) if os.path.isdir(os.path.join(sys.argv[1], d))))
