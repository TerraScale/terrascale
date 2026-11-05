import json,pathlib,subprocess,sys,shlex,urllib.request,time
import account_database as p
database=json.loads((p.root/(p.tag+'.proof.json')).read_text())['database']
base='/v1/projects/'+p.state['projectId']+'/databases/'+database
s,b,_,_=p.call('gru',base+'/tables/items/read-grant','PUT',{'where':'id >= 1','columns':['value']})
if s!=200: raise RuntimeError(('grant',s,b))
request=urllib.request.Request('https://auth.terrascale.tech/v1/terrascale/projects/'+p.state['projectId']+'/end-user-tokens',json.dumps({'user_id':p.key[:26],'database':database,'tables':['items']}).encode(),{'Authorization':'Bearer '+p.key,'Content-Type':'application/json'})
with urllib.request.urlopen(request,timeout=30) as response: bearer=json.load(response)['access_token']
for region in ['sjc','fra']:
 result=subprocess.run(['ssh','-o','BatchMode=yes','ts-'+region+'-1','python3','-c',shlex.quote(pathlib.Path(__file__).with_name('regional_client.py').read_text())],input=json.dumps({'bearer':bearer,'write_bearer':p.bearer,'project':p.state['projectId'],'region':region,'projected':True,'repeats':10}),text=True,capture_output=True,timeout=180)
 print(result.stdout,end='',flush=True)
 if result.returncode:
  print(result.stderr,file=sys.stderr)
  sys.exit(result.returncode)
