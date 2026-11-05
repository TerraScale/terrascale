import concurrent.futures,json,sys,time,urllib.error,urllib.request
cfg=json.load(sys.stdin)
base='/v1/projects/'+cfg['project']+'/databases/f9f5'
query='&where=id%3E-'+str(time.time_ns())+('&columns=value' if cfg.get('projected') else '')
def call(region,path,body=None):
 data=None if body is None else json.dumps(body).encode()
 auth=cfg.get('write_bearer',cfg['bearer']) if body is not None else cfg['bearer']
 request=urllib.request.Request('https://'+region+'.api.terrascale.tech'+path,data,{'Authorization':'Bearer '+auth,'Content-Type':'application/json'})
 started=time.perf_counter()
 try:
  with urllib.request.urlopen(request,timeout=35) as response:
   return response.status,json.load(response),dict(response.headers),1000*(time.perf_counter()-started)
 except urllib.error.HTTPError as error:
  return error.code,json.load(error),dict(error.headers),1000*(time.perf_counter()-started)
def serving(headers):
 return next((value for key,value in headers.items() if key.lower()=='x-terrabase-region'),None)
def shape(offset,live=False):
 return base+'/tables/items/shape?handle='+handle+'&offset='+offset+('&live=true' if live else '')+query
s,b,h,ms=call(cfg['region'],base+'/tables/items/shape?offset=-1'+query)
if s!=200: raise RuntimeError(('bootstrap',cfg['region'],s,b))
handle=b['handle'];offset=b['next_offset']
for attempt in range(16):
 s,b,h,ms=call(cfg['region'],shape(offset))
 if s!=200: raise RuntimeError(('catchup',s,b))
 offset=b['next_offset']
 if b.get('up_to_date'): break
else: raise RuntimeError('shape catchup exceeded limit')
samples=[]
for repeat in range(cfg.get('repeats',30)):
 value='regional-proof-'+str(time.time_ns())
 with concurrent.futures.ThreadPoolExecutor() as executor:
  request_offset=offset
  future=executor.submit(call,cfg['region'],shape(offset,True))
  time.sleep(0.1)
  started=time.perf_counter()
  ws,wb,wh,wm=call('gru',base+'/sql/execute',{'statements':[{'sql':'UPDATE items SET value=? WHERE id=1','args':[value]}]})
  if ws!=200: raise RuntimeError(('write',ws,wb))
  chunks=[]
  for attempt in range(16):
   s,b,h,tm=future.result(timeout=35)
   if s!=200: raise RuntimeError(('tail',offset,s,b))
   if serving(h)!=cfg['region']: raise RuntimeError(('tail forwarded',serving(h)))
   operations=[op for change in b['changes'] for op in change['ops']]
   if cfg.get('projected') and any(set(op.get('row',{}))-{'value'} for op in operations): raise RuntimeError('projection exposed ungranted columns')
   chunks.append({'offset':request_offset,'next_offset':b['next_offset'],'operations':len(operations),'request_ms':round(tm,1)})
   offset=b['next_offset']
   if any(op.get('row',{}).get('value')==value for op in operations): break
   if time.perf_counter()-started>90: raise RuntimeError(('update deadline',chunks))
   request_offset=offset
   future=executor.submit(call,cfg['region'],shape(offset,True))
  else: raise RuntimeError(('update not observed',chunks))
  elapsed=1000*(time.perf_counter()-started)
 rs,rb,rh,rm=call(cfg['region'],shape(request_offset,True))
 if rs!=200 or serving(rh)!=cfg['region']: raise RuntimeError(('replay',rs,rb,serving(rh)))
 replay=[op for change in rb['changes'] for op in change['ops']]
 if not any(op.get('row',{}).get('value')==value for op in replay): raise RuntimeError('replay omitted committed value')
 sample={'repeat':repeat+1,'serving_region':serving(h),'serving_machine':next((v for k,v in h.items() if k.lower()=='x-terrabase-machine'),None),'write_ms':round(wm,1),'write_start_to_tail_ms':round(elapsed,1),'replay_ms':round(rm,1),'chunks':chunks}
 samples.append(sample)
 print(json.dumps({'sample':sample,'client_region':cfg['region'],'end_user':bool(cfg.get('write_bearer'))}),flush=True)
print(json.dumps({'client_region':cfg['region'],'end_user':bool(cfg.get('write_bearer')),'samples':samples}),flush=True)
