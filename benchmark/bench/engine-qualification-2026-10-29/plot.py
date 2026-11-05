import json
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

root = Path(__file__).resolve().parent
cases = json.loads((root / 'cdc/results.json').read_text())['growth']['cases']
captured = [c for c in cases if c['capture']]
rates = [1, 10, 100]
logical, allocated = [], []
for rate in rates:
    rows = [c['result'] for c in captured if c['rate'] == rate]
    costs = [tuple((r['final']['turso_cdc'][k] - r['baseline']['turso_cdc'][k]) / r['writes'] for k in ('recordPayload', 'allocated')) for r in rows]
    assert len(costs) == 9 and len(set(costs)) == 1
    logical.append(costs[0][0] * rate * 86400 / 2**30)
    allocated.append(costs[0][1] * rate * 86400 / 2**30)

fig, (left, right) = plt.subplots(1, 2, figsize=(11, 4.8), layout='constrained')
fig.suptitle('CDC full: insert cost and offered-rate limits', fontsize=15)
left.bar([i - .18 for i in range(3)], logical, .36, label='Logical CDC records', color='#2563eb')
left.bar([i + .18 for i in range(3)], allocated, .36, label='Owned CDC file pages', color='#14b8a6')
left.set(xticks=range(3), xticklabels=rates, xlabel='Offered writes/s', ylabel='Conditional GiB/day (log scale)', title='30-second samples, extrapolated to one day')
left.set_yscale('log')
left.legend(fontsize=8)
versions = ['0.8.2', '0.8.3', '0.9.0-pre.2']
for i, version in enumerate(versions):
    rows = sorted([c for c in captured if c['rate'] == 100 and c['version'] == version], key=lambda c: c['repeat'])
    right.scatter([i - .12, i, i + .12], [c['result']['writes'] / c['result']['timing']['elapsed'] for c in rows], s=55, label=version)
right.axhline(100, color='#64748b', linestyle='--', linewidth=1)
right.set(xticks=range(3), xticklabels=versions, ylim=(70, 104), ylabel='Achieved writes/s over full run', title='100 writes/s offered; three repeats per version')
for axis in (left, right):
    axis.grid(axis='y', alpha=.2)
    axis.set_axisbelow(True)
fig.supxlabel('1,024-byte text + 32-byte blob + 26-character key; one INSERT/autocommit.\nNo retention. Shared x64 host; these samples do not qualify sustained 100 writes/s.', fontsize=9)
fig.savefig(root / 'cdc-growth.png', dpi=160, metadata={'Software': 'matplotlib ' + matplotlib.__version__})
