"""Replay the observed identity quota failure and enforce failed-status gating."""
import json
import os
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile

ship = Path(sys.argv[1]).resolve()
runner = runpy.run_path(str(ship))['FAST_RUNNER']
sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
with tempfile.TemporaryDirectory(prefix='beta-setup-gh-') as temporary:
    fake = Path(temporary) / 'gh'
    fake.write_text('''#!/usr/bin/env python3
import os, sys
if 'user' in sys.argv and os.environ['PROOF_FAILURE'] != 'identity':
    print('MarioGK')
    sys.exit(0)
print('gh: API rate limit exceeded (HTTP 403)', file=sys.stderr)
sys.exit(1)
''')
    fake.chmod(0o700)
    for failure, area in [('identity', 'setup'), ('status', 'probe')]:
        result = subprocess.run(
            [sys.executable, '-c', runner, 'terrascale', sha, json.dumps([['probe', ':']])],
            env=dict(os.environ, PATH=temporary + os.pathsep + os.environ['PATH'], PROOF_FAILURE=failure),
            text=True, capture_output=True)
        markers = [json.loads(line.removeprefix('@@TS_SHIP_FAILED '))
                   for line in result.stdout.splitlines() if line.startswith('@@TS_SHIP_FAILED ')]
        assert result.returncode == 2, result
        assert len(markers) == 1 and markers[0]['area'] == area, result
        assert 'API rate limit exceeded' in markers[0]['failure'], result
        assert '@@TS_SHIP_PASSED' not in result.stdout, result
        print(f'{failure}: exit 2; {area} failure reported; no success proof')
