import gzip
import base64
import hashlib
import json
import math
from pathlib import Path
import re
import sys


def percentile(values, p):
    return sorted(values)[math.ceil(len(values) * p) - 1]


def audit(path):
    with gzip.open(path, 'rt') as stream:
        run = json.load(stream)
    assert len(run['cases']) == 48
    assert run['root_network_before'] == run['root_network_after']
    assert run['clock_bracket'][0] <= run['clock_bracket'][1] <= run['clock_bracket'][2]
    assert not set(run['cleanup']['namespaces']) & {s.split()[0] for s in run['cleanup']['remaining'].splitlines()}
    for name in ('lab.py', 'pull.mjs', 'bridge.rs', 'prepare.py', 'shim.toml', 'Cargo.lock.gz', 'package-lock.json', 'package.json', 'build.sh'):
        assert hashlib.sha256(base64.b64decode(run['source_snapshots_base64'][name])).hexdigest() == run['sources_sha256'][name], name
    seen, results = set(), []
    for case in run['cases']:
        key = (case['delay_ms'], case['rate'], case['repeat'], case['mode'])
        assert key not in seen
        seen.add(key)
        source = case['source']['samples']
        endpoint = case['pull' if case['mode'] in ('page', 'combined') else 'destination']['samples']
        n = run['seconds'] * case['rate']
        assert len(source) == len(endpoint) == n
        assert case['source']['nice'] == 19
        expected = list(range(1, n + 1))
        for name in ('source', 'destination', 'pull'):
            if name not in case:
                continue
            block = case[name]
            assert block['rows'] == case['rate'] * (run['seconds'] + 2)
            assert [s[0] for s in block['samples']] == expected
            assert [s[0] for s in block['warmup_samples']] == list(range(-2 * case['rate'], 0))
            assert block['hash'] == case['source']['hash']
            if block.get('settings'):
                assert block['settings']['journal_mode']['rows'][0][0]['value'] == 'wal'
                assert int(block['settings']['synchronous']['rows'][0][0]['value']) == run['synchronous']
        assert all(a[1] <= a[2] <= a[3] <= a[4] and a[3] <= b[-1] for a, b in zip(source, endpoint))
        assert all(s[1] == case['source']['planned_start_ns'] + i * 1_000_000_000 // case['rate'] for i, s in enumerate(source))
        drops = [int(d) for q in case['qdisc'] for d in re.findall(r'dropped (\d+)', q)]
        assert drops and max(drops) == 0
        delays = [float(d) for q in case['qdisc'] for d in re.findall(r'delay ([\d.]+)ms', q)]
        assert delays == [case['delay_ms']] * 6
        assert sum(b[-1] for b in case['source']['batches']) == (case['rate'] * (run['seconds'] + 2) if case['mode'] != 'page' else 0)
        lag = [(b[-1] - a[3]) / 1e6 for a, b in zip(source, endpoint)]
        due = [(b[-1] - a[1]) / 1e6 for a, b in zip(source, endpoint)]
        results.append({'profile': 'FULL' if run['synchronous'] == 2 else 'NORMAL', 'delay': key[0], 'rate': key[1], 'repeat': key[2], 'mode': key[3], 'n': n, 'source_cps': n * 1e9 / (source[-1][3] - source[0][1] + 1e9 / case['rate']), 'source_span_cps': (n - 1) * 1e9 / (source[-1][3] - source[0][3]), 'destination_span_cps': (n - 1) * 1e9 / (endpoint[-1][-1] - endpoint[0][-1]), 'p50_ms': percentile(lag, .5), 'p99_ms': percentile(lag, .99), 'due_p99_ms': percentile(due, .99), 'writer_queue': case['source']['metrics']['writer_queue_max'], 'stream_queue': case['source']['metrics']['stream_queue_max']})
    assert seen == {(d, r, t, m) for d in (70, 90) for r in (100, 1000) for t in (1, 2, 3) for m in ('page', 'push', 'direct', 'combined')}
    return results


if __name__ == '__main__':
    paths = [path for path in sys.argv[1:] if path != '--check']
    results = [r for path in paths for r in audit(path)]
    print('| Profile | One-way ms | Offered commits/s | Path | Samples/repeat | Source commits/s R1 / R2 / R3 | Lag p50 / p99 ms R1; R2; R3 | Scheduled p99 ms R1 / R2 / R3 |')
    print('|---|---:|---:|---|---:|---|---|---|')
    for key in sorted({(r['profile'], r['delay'], r['rate'], r['mode']) for r in results}):
        rows = sorted([r for r in results if (r['profile'], r['delay'], r['rate'], r['mode']) == key], key=lambda r: r['repeat'])
        cps = ' / '.join(f"{r['source_cps']:.1f}" for r in rows)
        lag = '; '.join(f"{r['p50_ms']:.1f} / {r['p99_ms']:.1f}" for r in rows)
        due = ' / '.join(f"{r['due_p99_ms']:.1f}" for r in rows)
        print(f'| {key[0]} | {key[1]} | {key[2]} | {key[3]} | {rows[0]["n"]} | {cps} | {lag} | {due} |')
    destination = Path(paths[0]).parent / '2026-10-29-lag-lab-summary.json.gz'
    if '--check' in sys.argv[1:]:
        with gzip.open(destination, 'rt') as stream:
            if json.load(stream) != results:
                raise ValueError('Published summary differs from audited evidence')
    else:
        destination.write_bytes(gzip.compress(json.dumps(results, separators=(',', ':')).encode(), compresslevel=9, mtime=0))
