import collections
import gzip
import json
from pathlib import Path
import sys


checks = ['connect', 'introspect', 'migrate_table', 'migrate_fk', 'migrate_index', 'crud', 'transactions', 'json', 'prepared', 'errors']
tools = ['prisma', 'drizzle', 'efcore', 'sqlalchemy', 'psql', 'pgcli']
records = {}
for filename in sys.argv[1:]:
    path = Path(filename)
    content = gzip.decompress(path.read_bytes()).decode() if path.suffix == '.gz' else path.read_text()
    for line in content.splitlines():
        row = json.loads(line)
        if row['tool'].lower() not in tools or row['check'] not in checks + ['cleanup', 'cleanup_connection']:
            raise ValueError(f'unknown client/check: {row}')
        if row['check'] not in checks:
            continue
        key = (row['surface'], row['tool'].lower(), row['repeat'], row['check'])
        if key in records or row['status'] not in {'pass', 'fail', 'blocked'} or row['repeat'] not in {1, 2, 3}:
            raise ValueError(f'invalid or duplicate measurement: {key}')
        records[key] = row
if not records:
    raise ValueError('no measurements')
print('| Surface | Client | ' + ' | '.join(checks) + ' |')
print('|---|---|' + '---|' * len(checks))
missing = False
for surface in sorted({key[0] for key in records}):
    for tool in tools:
        cells = []
        for check in checks:
            counts = collections.Counter(records.get((surface, tool, repeat, check), {}).get('status', 'missing') for repeat in (1, 2, 3))
            cells.append(', '.join(f'{status} {count}/3' for status, count in sorted(counts.items())))
            missing |= bool(counts['missing'])
        print(f'| {surface} | {tool} | ' + ' | '.join(cells) + ' |')
print('\nFailures and prerequisite blockers:\n')
reasons = collections.defaultdict(set)
for (surface, tool, repeat, check), row in records.items():
    if row['status'] != 'pass':
        detail = row['detail'] if isinstance(row['detail'], str) else json.dumps(row['detail'], sort_keys=True)
        reasons[(surface, tool, check)].add(detail.replace('\n', ' ').replace('|', '\\|'))
for (surface, tool, check), details in sorted(reasons.items()):
    print(f'- {surface} / {tool} / {check}: ' + '; '.join(sorted(details)))
if missing:
    print('\nINCOMPLETE: at least one client/check/repeat has no measurement.')
sys.exit(int(missing))
