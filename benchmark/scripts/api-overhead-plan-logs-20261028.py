"""Inspect the failed setup interval on-host; export no raw log messages or identifiers."""
import datetime
import json
from pathlib import Path
import subprocess

remote = r'''
import collections,json,re,subprocess
def read(args): return subprocess.check_output(args).decode()
tasks = {row.split()[0] for row in read(['sudo','-n','ctr','-n','terrascale','tasks','ls']).splitlines()[1:]
         if len(row.split()) >= 3 and row.split()[2] == 'RUNNING'}
containers = [row.split() for row in read(['sudo','-n','ctr','-n','terrascale','containers','ls']).splitlines()[1:] if len(row.split()) >= 2]
matches = [row for row in containers if '/terrabase@' in row[1]]
if not matches or len(matches) > 16: raise SystemExit('Expected a bounded retained TerraBase container list')
known = {'storage_busy','catalog_stale','confirmation_busy','disk_capacity','copy_faulted','capacity',
         'remote_region_unavailable','auth_unavailable','invalid_request','invalid_key','not_found','internal_error','unavailable'}
records, files = [], []
for container, stream in [(container, stream) for container in matches for stream in ('stdout','stderr')]:
    path = '/var/lib/ts-orch/runtime-logs/' + container[0] + '/' + stream
    result = subprocess.run(['sudo','-n','tail','-n','20000',path],capture_output=True,text=True)
    scanned = window = 0
    times = []
    for line in result.stdout.splitlines():
        try: row = json.loads(line[line.index('{'):])
        except (ValueError,TypeError): continue
        if not isinstance(row,dict): continue
        scanned += 1
        stamp = str(row.get('time',''))
        if re.fullmatch(r'[0-9T:.+Z-]{10,40}',stamp): times.append(stamp)
        if not ('2026-10-28T09:19:35' <= stamp[:19] <= '2026-10-28T09:20:57'): continue
        window += 1
        endpoint = str(row.get('Endpoint',''))
        plan = 'SetStorageOrganizationPlan' in endpoint or '/admin/organizations/{organization}/plan' in endpoint
        error = str(row.get('error',''))
        types = sorted(set(re.findall(r'\b(?:System|TerraBase|TerraScale)\.(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*(?:Exception|Error)\b',error)))
        codes = sorted({value for key,value in row.items() if key in ('code','Code','ErrorCode','errorCode') and isinstance(value,str) and value in known})
        codes += sorted(code for code in known if code not in codes and re.search(r'(?<![a-z_])'+code+r'(?![a-z_])',error))
        if plan or types or codes:
            safe_time = stamp if re.fullmatch(r'[0-9T:.+Z-]{10,40}',stamp) else None
            records.append({'image':container[1],'time':safe_time,'matches_plan_route':plan,'exception_types':types,'known_codes':codes})
    files.append({'image':container[1],'running':container[0] in tasks,'stream':stream,'readable':result.returncode == 0,'json_rows':scanned,'rows_in_setup_interval':window,
                  'timestamped_rows':len(times),'first_time':min(times) if times else None,'last_time':max(times) if times else None})
print(json.dumps({'containers':len(matches),'files':files,'records':records}))
'''
base = Path('/home/mariogk/.local/state/perf-api-overhead')
command = [str(base / 'ts-orch'), 'netbird', 'ssh', '--vault-id',
    'ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq',
    '--netbird', str(base / 'netbird-operator'), '--', '--known-hosts', str(base / 'known_hosts'),
    '--identity', '/home/mariogk/.ssh/homelab_workstation_ed25519', 'ubuntu@ts-gru-1', 'python3', '-']
result = subprocess.run(command, input=remote, text=True, capture_output=True, timeout=45)
if result.returncode:
    raise SystemExit('Allowlisted plan-log inspection failed')
row = json.loads(result.stdout)
row['observed_at'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
with Path('/tmp/perf-api-plan-log-fields-20261028-c.json').open('x') as output:
    json.dump(row, output, indent=2)
    output.write('\n')
print(json.dumps(row))
