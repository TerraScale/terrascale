import json,subprocess,sys,time,shlex
import account_database as p
base='/v1/projects/'+p.state['projectId']+'/databases/f9f5/sql/execute'
def execute(statements):
 status,body,headers,ms=p.call('gru',base,'POST',{'statements':statements})
 if status!=200: raise RuntimeError((status,body))
 return ms
def metrics():
 result=subprocess.run(['ssh','-o','BatchMode=yes','ts-gru-1','bash','-s'],input=__import__('pathlib').Path(__file__).with_name('metrics.sh').read_text(),text=True,capture_output=True,timeout=20)
 if result.returncode: raise RuntimeError(result.stderr)
 lines=result.stdout.strip().splitlines()
 rows=next((float(line.split()[-1]) for line in lines if line.startswith('terrabase_cdc_retained_rows{') or line.startswith('terrabase_cdc_retained_rows ')),None)
 if rows is None: raise RuntimeError('CDC gauge missing')
 public=json.loads((p.root/(p.tag+'.proof.json')).read_text())
 name=public['database']+'.db'
 command=shlex.join(['sudo','-n','find','/var/lib/ts-data/terrabase','-type','f','(','-name',name,'-o','-name',name+'-wal',')','-printf','%p %s\n'])
 sizes=subprocess.run(['ssh','-o','BatchMode=yes','ts-gru-1',command],text=True,capture_output=True,timeout=20)
 storage={line.rsplit(' ',1)[0].rsplit('/',1)[-1]:int(line.rsplit(' ',1)[1]) for line in sizes.stdout.splitlines()}
 if not storage: raise RuntimeError('database file sizes unavailable')
 return {'at':time.time(),'rows':rows,'storage_bytes':storage,'metrics':lines}
if sys.argv[1]=='prepare':
 print(json.dumps({'prepare_ms':execute([{'sql':'CREATE TABLE cdc_load(id INTEGER PRIMARY KEY, value TEXT)'},{'sql':'INSERT INTO cdc_load VALUES(1, ?)', 'args':['x'*1024]}]),'metrics':metrics()}),flush=True)
else:
 baseline=metrics();out=[]
 for repeat in range(3):
  before=metrics();started=time.perf_counter();times=[]
  for batch in range(16):
   times.append(execute([{'sql':'UPDATE cdc_load SET value=? WHERE id=1','args':[f'{repeat}-{batch}-{row}-'+'x'*1024]} for row in range(64)]))
  duration=time.perf_counter()-started
  observations=[]
  for attempt in range(24):
   current=metrics();observations.append(current)
   if current['rows']<=baseline['rows']+256: break
   time.sleep(5)
  else: raise RuntimeError(('CDC did not drain after acknowledged backup and replica pulls',observations))
  item={'repeat':repeat+1,'writes':1024,'payload_bytes':1024,'elapsed_ms':round(duration*1000,1),'writes_per_second':round(1024/duration,1),'request_ms':times,'before':before,'after':observations}
  out.append(item);print(json.dumps({'load_repeat':item}),flush=True)
 print(json.dumps({'baseline':baseline,'load_repeats':out}),flush=True)
