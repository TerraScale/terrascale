"""Summarize all retained writes, with conservative observed-order p99."""
import argparse
import gzip
import json
import math
import re
import statistics
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('input', type=Path)
args = parser.parse_args()
reader = gzip.open if args.input.suffix == '.gz' else open
with reader(args.input, 'rt') as source:
    rows = [json.loads(line) for line in source]

def summary(values):
    ordered = sorted(values)
    return {'count': len(ordered), 'p50_ms': round(statistics.median(ordered), 3),
            'p99_ms': round(ordered[math.ceil(.99 * (len(ordered) - 1))], 3),
            'max_ms': round(ordered[-1], 3)} if ordered else None

def stages(row):
    return {name: float(duration) for name, duration in re.findall(
        r'([\w_]+);dur=([\d.]+)', row.get('headers', {}).get('server-timing', ''))}

result = {'input': str(args.input), 'percentile': 'sorted[ceil(q*(n-1))]; all samples retained',
          'environment': [row for row in rows if row['kind'] == 'environment'], 'regions': {}}
for region in ('gru', 'sjc', 'fra'):
    writes = [row for row in rows if row['kind'] == 'write' and row['region'] == region]
    baselines = [row['ms'] for row in rows if row['kind'] == 'baseline' and row['region'] == region]
    region_result = {'baseline': summary(baselines), 'failed_writes': sum(not row['ok'] for row in writes),
                     'client': summary([row['ms'] for row in writes]),
                     'server': summary([stages(row)['app'] for row in writes if 'app' in stages(row)]),
                     'first_insert': writes[0] if writes else None, 'repeats': {}}
    if baselines:
        region_result['client_budget_ms'] = round(statistics.median(baselines) + 100, 3)
    for repeat in (1, 2, 3):
        selected = [row for row in writes if row['repeat'] == repeat]
        region_result['repeats'][repeat] = {
            'client': summary([row['ms'] for row in selected]),
            'server': summary([stages(row)['app'] for row in selected if 'app' in stages(row)]),
            'stages': {name: summary([stages(row)[name] for row in selected if name in stages(row)])
                       for name in ('auth', 'local_allowance', 'local_native', 'local_execute', 'local_begin',
                                    'local_seal', 'local_hlc', 'push_publish', 'push_hold', 'local_commit')},
            'routes': sorted({row.get('headers', {}).get('terrascale-write-route', '') for row in selected})}
    result['regions'][region] = region_result
result['inventories'] = [row for row in rows if row['kind'] == 'inventory']
result['cross_reads'] = {'count': sum(row['kind'] == 'cross_read' for row in rows),
                       'failures': sum(row['kind'] == 'cross_read' and not row['ok'] for row in rows),
                       'empty': sum(row['kind'] == 'cross_read' and not row.get('rows') for row in rows)}
print(json.dumps(result, indent=2))
