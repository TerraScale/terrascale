import base64,json,pathlib,urllib.request,urllib.parse,urllib.error,time,os,sys
tag=os.environ['F9F5_PROOF_TAG']
root=pathlib.Path.home()/'.local/state/ts-throwaway-account'
state=json.loads((root/(tag+'.json')).read_text())
key=(root/(tag+'.key')).read_text().strip()
def refresh_bearer():
 global bearer
 form=urllib.parse.urlencode({'grant_type':'client_credentials','project':state['projectId']}).encode()
 headers={'Authorization':'Basic '+base64.b64encode((key[:26]+':'+key).encode()).decode(),'Content-Type':'application/x-www-form-urlencoded'}
 with urllib.request.urlopen(urllib.request.Request('https://auth.terrascale.tech/terrascale/oauth/token',form,headers),timeout=20) as response: bearer=json.load(response)['access_token']
 target=root/(tag+'.token');target.touch(mode=0o600,exist_ok=True);target.chmod(0o600);target.write_text(bearer)
refresh_bearer()
def call(region,path,method='GET',body=None):
 data=None if body is None else json.dumps(body).encode()
 h={'Authorization':'Bearer '+bearer,'Content-Type':'application/json'}
 req=urllib.request.Request('https://'+region+'.api.terrascale.tech'+path,data,h,method=method)
 started=time.perf_counter()
 try:
  with urllib.request.urlopen(req,timeout=35) as r:
   payload=r.read()
   return r.status,json.loads(payload) if payload else None,dict(r.headers),1000*(time.perf_counter()-started)
 except urllib.error.HTTPError as e:
  return e.code,json.load(e),dict(e.headers),1000*(time.perf_counter()-started)
if __name__=='__main__':
 path='/v1/projects/'+state['projectId']+'/databases'
 status,body,h,elapsed=call('gru',path,'POST',{'name':'f9f5','homeRegion':'gru','replicaRegions':['sjc','fra']})
 if status!=201:
  print(json.dumps({'status':status,'code':body.get('error',{}).get('code'),'message':body.get('error',{}).get('message')}));sys.exit(1)
 public={'project':state['projectId'],'organization':state['organizationId'],'database':body['id'],'name':'f9f5'}
 if body['homeRegion']!='gru' or set(body['replicaRegions'])!={'sjc','fra'}: raise RuntimeError(('placement',body))
 (root/(tag+'.proof.json')).write_text(json.dumps(public))
 p=path+'/f9f5/sql/execute'
 status,body,_,elapsed=call('gru',p,'POST',{'statements':[{'sql':'CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT)'},{'sql':"INSERT INTO items VALUES (1,'initial')"}]})
 print(json.dumps({'create_database':201,'create_table':status,'home_region':body['homeRegion'] if 'homeRegion' in body else 'gru','ms':elapsed,'database':public['database']}),flush=True)
