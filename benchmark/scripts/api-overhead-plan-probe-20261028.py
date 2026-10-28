"""Read-only internal plan-route diagnostic; emits only allowlisted response fields."""
import datetime
import importlib.util
import json
from pathlib import Path
import subprocess

helper = Path(__file__).with_name('api-overhead-plan.py')
spec = importlib.util.spec_from_file_location('plan_helper', helper)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
# Reuse on-host credential/peer discovery, then GET a route that only supports PUT.
# No canonical organization id, body or mutation is sent.
remote = module.REMOTE.split('with tempfile.NamedTemporaryFile() as header:', 1)[0] + r'''
with tempfile.NamedTemporaryFile() as header:
    header.write(b'X-TerraBase-Peer-Credential: ' + credential + b'\n')
    header.flush()
    result = subprocess.run(['curl', '--silent', '--show-error', '--http2-prior-knowledge', '--max-time', '15',
        '--header', '@' + header.name, '--request', 'GET', '--write-out', '\n%{http_code}',
        'http://' + addresses[0] + ':8081/admin/organizations/invalid/plan'], capture_output=True)
body, _, status = result.stdout.rpartition(b'\n')
code = None
if result.returncode == 0:
    try:
        value = json.loads(body)
        error = value.get('error') if isinstance(value, dict) else None
        code = error.get('code') if isinstance(error, dict) else error
    except (ValueError, TypeError):
        pass
known = {'invalid_request', 'invalid_key', 'unknown_route', 'method_not_allowed', 'internal_error', 'unavailable',
         'storage_busy', 'catalog_stale', 'confirmation_busy', 'disk_capacity', 'copy_faulted', 'capacity',
         'remote_region_unavailable', 'auth_unavailable'}
print(json.dumps({'http': int(status) if result.returncode == 0 and status.isdigit() else None,
    'curl_exit': result.returncode, 'code': code if isinstance(code, str) and code in known else 'other'}))
'''
base = Path('/home/mariogk/.local/state/perf-api-overhead')
rows = []
for node in ('ts-gru-1', 'ts-sjc-1', 'ts-fra-1'):
    command = [str(base / 'ts-orch'), 'netbird', 'ssh', '--vault-id',
        'ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq',
        '--netbird', str(base / 'netbird-operator'), '--', '--known-hosts', str(base / 'known_hosts'),
        '--identity', '/home/mariogk/.ssh/homelab_workstation_ed25519', 'ubuntu@' + node, 'python3', '-', 'invalid']
    result = subprocess.run(command, input=remote, text=True, capture_output=True, timeout=40)
    if result.returncode:
        raise SystemExit('Read-only plan-route probe failed for ' + node)
    row = json.loads(result.stdout)
    row.update(peer=node, observed_at=datetime.datetime.now(datetime.timezone.utc).isoformat())
    rows.append(row)
    print(json.dumps(row), flush=True)
with Path('/tmp/perf-api-plan-route-20261028-a.json').open('x') as output:
    json.dump(rows, output, indent=2)
    output.write('\n')
