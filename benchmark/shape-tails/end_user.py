import json
import pathlib
import shlex
import subprocess
import sys
import urllib.request

import account_database as p

database=json.loads((p.root/(p.tag+'.proof.json')).read_text())['database']
base='/v1/projects/'+p.state['projectId']+'/databases/'+database
s,b,_,_=p.call('gru',base+'/rules')
precondition={'If-Match':'"'+b['version']+'"'} if s==200 else {'If-None-Match':'*'}
s,b,_,_=p.call('gru',base+'/rules','PUT',{'tables':{'items':{'read':'row.id >= 1','columns':['value']}}},precondition)
if s!=200: raise RuntimeError(('rules',s,b))
request=urllib.request.Request('https://auth.terrascale.tech/v1/'+p.state['projectId']+'/custom-tokens',json.dumps({'user_id':p.key.removeprefix('sk_')[:26],'database':database}).encode(),{'Authorization':'Bearer '+p.key,'Content-Type':'application/json'})
with urllib.request.urlopen(request,timeout=30) as response: bearer=json.load(response)['access_token']
for region in ['sjc','fra']:
 result=subprocess.run(['ssh','-o','BatchMode=yes','ts-'+region+'-1','python3','-c',shlex.quote(pathlib.Path(__file__).with_name('regional_client.py').read_text())],check=False,input=json.dumps({'bearer':bearer,'write_bearer':p.bearer,'project':p.state['projectId'],'region':region,'projected':True,'repeats':10}),text=True,capture_output=True,timeout=180)
 print(result.stdout,end='',flush=True)
 if result.returncode:
  print(result.stderr,file=sys.stderr)
  sys.exit(result.returncode)
