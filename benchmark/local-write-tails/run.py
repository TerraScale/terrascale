import concurrent.futures
import dataclasses
import json
import math
import os
from pathlib import Path
import re
import subprocess
import threading
import time

import httpx
from terrascale import TerraScale, new_id

import argparse
parser = argparse.ArgumentParser()
parser.add_argument('--tag', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
root = Path.home() / '.local/state/ts-throwaway-account'
state = json.loads((root / (args.tag + '.json')).read_text())
config = {'key': (root / (args.tag + '.key')).read_text().strip(),
          'project': state['projectId'], 'database': 'localtails'}
output = open(args.output, 'w')
REGIONS = ['gru', 'sjc', 'fra']
events = []
lock = threading.Lock()

def emit(value):
    with lock:
        events.append(value)
        output.write(json.dumps(value) + "\n")
        output.flush()

def client(region, headers=None):
    last = {}
    def response(r):
        if r.request.url.host.endswith('api.terrascale.tech'):
            last.clear()
            last.update(status=r.status_code, headers={k:v for k,v in r.headers.items() if k.startswith(('x-terrabase', 'terrascale-', 'server-timing', 'cf-ray', 'cf-cache-status', 'age', 'x-request-id'))})
    h = httpx.Client(timeout=30, http2=True, headers=headers, event_hooks={'response':[response]})
    c = TerraScale(config['key'], project=config['project'], base_url=f'https://{region}.api.terrascale.tech', region=region, http_client=h, max_attempts=1)
    return c, h, last

def attempt(kind, region, fn, last, **extra):
    started = time.perf_counter()
    try:
        value = fn()
        ok = True
        error = None
    except Exception as e:
        value = None
        ok = False
        error = {'type':type(e).__name__, 'status':getattr(e,'status',None), 'code':getattr(e,'code',None), 'request_id':getattr(e,'request_id',None)}
    elapsed = (time.perf_counter()-started)*1000
    item = {'kind':kind,'region':region,'ok':ok,'ms':round(elapsed,3),'completed_at':time.time(),**dict(last),**extra}
    if error: item['error']=error
    if isinstance(value,list): item['rows']=[dict(x) for x in value]
    elif dataclasses.is_dataclass(value) and kind != 'write': item['value']=dataclasses.asdict(value)
    emit(item)
    return value,ok

def usage(label):
    for region in REGIONS:
        c,h,last=client(region)
        try:
            r=h.get(f'{c.base_url}/v1/projects/{config["project"]}/databases/{config["database"]}/usage', headers={'Authorization':'Bearer '+c._bearer()})
            emit({'kind':'usage','label':label,'region':region,'status':r.status_code,'body':r.json()})
        finally: h.close()

def worker(source):
    clients = {r:client(r) for r in REGIONS}
    try:
        for r,(c,h,last) in clients.items():
            attempt('warmup',r,lambda c=c:c.database(config['database']).query('SELECT 1 AS ready'),last,source=source)
        c,h,last=clients[source]
        for repeat in range(1,4):
            failures=0
            for n in range(100):
                key=new_id()
                _,ok=attempt('write',source,lambda:c.database(config['database']).execute('INSERT INTO acceptance_rows (id, writer, sequence, body) VALUES (?, ?, ?, ?)',[key,source,repeat*1000+n,'acceptance']),last,repeat=repeat,n=n,id=key)
                failures=0 if ok else failures+1
                if not ok:
                    if failures>=2:
                        emit({'kind':'stopped','source':source,'repeat':repeat,'reason':'two consecutive write failures'})
                        return
                    continue
                for target in REGIONS:
                    if target==source: continue
                    t,th,tl=clients[target]
                    attempt('cross_read',target,lambda:t.database(config['database']).query('SELECT id, writer, sequence, body FROM acceptance_rows WHERE id = ?',[key]),tl,source=source,repeat=repeat,n=n,id=key)
    finally:
        for c,h,last in clients.values():h.close()

if __name__=='__main__':
    emit({'kind':'environment','host':os.uname().nodename,'sdk_revision':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'at':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())})
    c,h,last=client('gru')
    try:
        token = c._bearer()
        response = h.post(f'{c.base_url}/v1/projects/{config["project"]}/databases', headers={'Authorization':'Bearer '+token},
                          json={'name':config['database'], 'homeRegion':'gru', 'allRegions':True})
        response.raise_for_status()
        emit({'kind':'created','database':response.json()['id']})
        c.database(config['database']).execute('CREATE TABLE acceptance_rows (id TEXT PRIMARY KEY, writer TEXT, sequence INTEGER, body TEXT)')
    finally:h.close()
    usage('before')
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        list(pool.map(worker,REGIONS))
    for region in REGIONS:
        for mode,headers in [('home-durable',{'TerraScale-Durability':'home'}),('forced-home',{'TerraScale-Write':'home'})]:
            c,h,last=client(region,headers)
            try:
                c.database(config['database']).query('SELECT 1')
                for n in range(10):
                    key=new_id()
                    _,ok=attempt(mode,region,lambda:c.database(config['database']).execute('INSERT INTO acceptance_rows (id, writer, sequence, body) VALUES (?, ?, ?, ?)',[key,region,n,mode]),last,id=key)
                    if not ok:break
            finally:h.close()
    for region in REGIONS:
        c,h,last=client(region)
        try:attempt('inventory',region,lambda:c.database(config['database']).query('SELECT writer, body, COUNT(*) AS count, COUNT(DISTINCT id) AS distinct_ids FROM acceptance_rows GROUP BY writer, body ORDER BY writer, body'),last)
        finally:h.close()
    usage('after')
    print(json.dumps({'output':args.output,'writes':sum(x['kind']=='write' for x in events),
                      'failures':sum(not x.get('ok',True) for x in events)}))
