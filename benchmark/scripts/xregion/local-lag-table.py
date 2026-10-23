#!/usr/bin/env python3
"""Markdown tables from a local-lag.sh output directory: write-to-visible per direction above the injected one-way delay,
the remote ack delay per direction, every replication step's duration and the catalog cache hit ratio, from the
difference of each node's /metrics before and after the probes.

usage: local-lag-table.py <out-dir>
"""

import collections
import glob
import json
import os
import re
import statistics
import sys

LINE = re.compile(r'^([a-z_]+)\{([^}]*)\} ([0-9.eE+-]+|NaN|\+Inf)')
LABEL = re.compile(r'(\w+)="([^"]*)"')


def pct(values, p):
    values = sorted(values)
    return values[min(len(values) - 1, int(round(p / 100 * (len(values) - 1))))]


def metrics(path):
    series = {}
    for line in open(path):
        m = LINE.match(line)
        if m:
            labels = tuple(sorted((k, v) for k, v in LABEL.findall(m.group(2)) if not k.startswith('otel_')))
            series[(m.group(1), labels)] = float(m.group(3))
    return series


def delta(directory, region):
    before = metrics(os.path.join(directory, f'metrics-before-{region}.txt'))
    after = metrics(os.path.join(directory, f'metrics-after-{region}.txt'))
    return {key: value - before.get(key, 0) for key, value in after.items()}


def histograms(series, name, key):
    """Per value of label `key`: (count, sum, [(le, cumulative)])."""
    out = collections.defaultdict(lambda: [0, 0, []])
    for (metric, labels), value in series.items():
        labels = dict(labels)
        if key not in labels:
            continue
        h = out[labels[key]]
        if metric == name + '_count':
            h[0] = value
        elif metric == name + '_sum':
            h[1] = value
        elif metric == name + '_bucket':
            h[2].append((float(labels['le']), value))
    return out


def quantile(buckets, count, q):
    """Linear interpolation inside the bucket holding the q-th observation, as Prometheus' histogram_quantile does."""
    buckets = sorted(buckets)
    rank, previous_le, previous = q * count, 0.0, 0.0
    for le, cumulative in buckets:
        if cumulative >= rank:
            if le == float('inf'):
                return previous_le
            inside = cumulative - previous
            return previous_le + (le - previous_le) * ((rank - previous) / inside if inside else 0)
        previous_le, previous = le, cumulative
    return previous_le


def main(directory):
    delays = {}
    for pair in open(os.path.join(directory, 'delays.txt')).read().strip().split(','):
        regions, ms = pair.split('=')
        a, b = regions.split('-')
        delays[(a, b)] = delays[(b, a)] = float(ms)
    print(f"Commit `{open(os.path.join(directory, 'commit.txt')).read().strip()[:10]}`, one-way delays (ms): "
          + ', '.join(f'{a}-{b} {ms}' for (a, b), ms in delays.items() if a < b))
    print()
    print('| Direction | Delay | Visible p50 | p95 | p99 | Overhead p50 | p95 | p99 | p50 per run (min-max) | Origin ack p50 | p99 | Probes |')
    print('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: |')
    rows = collections.defaultdict(lambda: collections.defaultdict(list))
    for path in sorted(glob.glob(os.path.join(directory, 'probe-*.jsonl'))):
        run, a, b = os.path.basename(path)[len('probe-'):-len('.jsonl')].split('-')
        for line in open(path):
            sample = json.loads(line)
            if 'summary' not in sample:
                rows[(a, b)][run].append(sample)
    for (a, b), runs in sorted(rows.items()):
        samples = [s for run in runs.values() for s in run]
        visible = [s['visible_ms'] for s in samples]
        over = [v - delays[(a, b)] for v in visible]
        ack = [s['ack_ms'] for s in samples]
        per_run = [statistics.median([s['visible_ms'] - delays[(a, b)] for s in run]) for run in runs.values()]
        print(f'| {a} → {b} | {delays[(a, b)]:.1f} | {pct(visible, 50):.1f} | {pct(visible, 95):.1f} | {pct(visible, 99):.1f} '
              f'| {pct(over, 50):.1f} | {pct(over, 95):.1f} | {pct(over, 99):.1f} '
              f'| {statistics.median(per_run):.1f} ({min(per_run):.1f}-{max(per_run):.1f}) | {pct(ack, 50):.1f} | {pct(ack, 99):.1f} | {len(samples)} |')

    regions = sorted({a for a, _ in rows})
    print()
    print('Remote ack delay (origin stamp → peer region durable ack, `terrabase_replication_remote_ack_delay_seconds`, bucket-interpolated, ms):')
    print()
    print('| Direction | Groups | Mean | p50 | p99 |')
    print('| --- | ---: | ---: | ---: | ---: |')
    steps = collections.defaultdict(dict)
    cache = collections.defaultdict(lambda: collections.Counter())
    for region in regions:
        series = delta(directory, region)
        for peer, (count, total, buckets) in sorted(histograms(series, 'terrabase_replication_remote_ack_delay_seconds', 'peer_region').items()):
            if count:
                print(f'| {region} → {peer} | {count:.0f} | {1000 * total / count:.1f} | {1000 * quantile(buckets, count, .5):.1f} '
                      f'| {1000 * quantile(buckets, count, .99):.1f} |')
        for step, h in histograms(series, 'terrabase_replication_step_duration_seconds', 'step').items():
            steps[step][region] = h
        for (metric, labels), value in series.items():
            if metric == 'terrabase_catalog_cache_lookups_total':
                labels = dict(labels)
                cache[(region, labels['cache'])][labels['result']] += value

    print()
    print('Step durations per node (`terrabase_replication_step_duration_seconds`; mean, bucket-interpolated p50 / p99, ms):')
    print()
    print('| Step | ' + ' | '.join(regions) + ' |')
    print('| --- |' + ' ---: |' * len(regions))
    for step in sorted(steps):
        cells = []
        for region in regions:
            count, total, buckets = steps[step].get(region, (0, 0, []))
            cells.append(f'{1000 * total / count:.2f} / {1000 * quantile(buckets, count, .5):.2f} / {1000 * quantile(buckets, count, .99):.1f} (n={count:.0f})'
                         if count else '-')
        print(f'| {step} | ' + ' | '.join(cells) + ' |')

    print()
    print('Catalog cache lookups during the probes (hit ratio, lookups):')
    print()
    print('| Node | Cache | Hit ratio | Lookups |')
    print('| --- | --- | ---: | ---: |')
    for (region, name), counts in sorted(cache.items()):
        total = counts['hit'] + counts['miss']
        if total:
            print(f'| {region} | {name} | {counts["hit"] / total:.3f} | {total:.0f} |')


if __name__ == '__main__':
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
