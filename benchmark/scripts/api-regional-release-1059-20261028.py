import json,subprocess,datetime
from pathlib import Path
remote=r'''
import json,subprocess,tempfile

def read(args):return subprocess.check_output(args)
tasks={r.split()[0]:r.split()[1] for r in read(['sudo','-n','ctr','-n','terrascale','tasks','ls']).decode().splitlines()[1:] if len(r.split())>=3 and r.split()[2]=='RUNNING'}
matches=[r.split()[0] for r in read(['sudo','-n','ctr','-n','terrascale','containers','ls']).decode().splitlines()[1:] if len(r.split())>=2 and '/terrabase@' in r.split()[1] and r.split()[0] in tasks]
if len(matches)!=1:raise SystemExit('Expected one running TerraBase container')
root='/proc/'+tasks[matches[0]]
env=dict(p.split(b'=',1) for p in read(['sudo','-n','cat',root+'/environ']).split(b'\0') if b'=' in p)
secret_path=env.get(b'TERRABASE_PEER_SECRET_FILE')
credential=read(['sudo','-n','cat',root+'/root'+secret_path.decode()]).strip() if secret_path else env[b'TERRABASE_PEER_SECRET']
peers=read(['sudo','-n','cat',root+'/root'+env[b'TS_PEERS_FILE'].decode()]).decode().splitlines()
addresses=[p.split()[2] for p in peers if len(p.split())==3 and p.split()[0]==env[b'TS_NODE'].decode()]
if len(addresses)!=1:raise SystemExit('Expected one admitted local node address')
with tempfile.NamedTemporaryFile() as header:
 header.write(b'X-TerraBase-Peer-Credential: '+credential+b'\n');header.flush()
 result=subprocess.run(['curl','--fail','--silent','--show-error','--http2-prior-knowledge','--max-time','30','--header','@'+header.name,'http://'+addresses[0]+':8081/healthz'],capture_output=True)
if result.returncode:raise SystemExit('Authenticated health probe failed')
body=json.loads(result.stdout)
print(json.dumps({k:body.get(k) for k in ('status','version','region','revision','machine','storageFormat')}))
'''
base='/home/mariogk/.local/state/perf-api-overhead/'
rows=[]
for node in ('ts-gru-1','ts-sjc-1','ts-fra-1'):
 r=subprocess.run([base+'ts-orch','netbird','ssh','--vault-id','ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq','--netbird',base+'netbird-operator','--','--known-hosts',base+'known_hosts','--identity','/home/mariogk/.ssh/homelab_workstation_ed25519','ubuntu@'+node,'python3','-'],input=remote,text=True,capture_output=True,timeout=55)
 if r.returncode:raise SystemExit('Authenticated region health failed for '+node)
 body=json.loads(r.stdout);body['observed_at']=datetime.datetime.now(datetime.timezone.utc).isoformat();body['peer']=node;rows.append(body);print(json.dumps(body),flush=True)
with Path('/tmp/perf-api-prod-revisions-post821-20261028T105943Z.json').open('x') as f: f.write(json.dumps(rows,indent=2)+'\n')
