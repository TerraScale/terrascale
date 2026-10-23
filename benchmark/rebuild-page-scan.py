import ctypes as C
from pathlib import Path
libpath=str(Path.home()/'.nuget/packages/turso.data.native/0.8.2/runtimes/linux-x64/native/libturso_sdk_kit.so')
l=C.CDLL(libpath); P=C.c_void_p
class Config(C.Structure):
 _fields_=[('async_io',C.c_uint64),('path',C.c_char_p),('features',P),('vfs',P),('cipher',P),('key',P),('codec',P),('flags',C.c_uint32)]
def fn(name,args,ret=C.c_uint32):
 f=getattr(l,'turso_'+name);f.argtypes=args;f.restype=ret;return f
new=fn('database_new',[C.POINTER(Config),C.POINTER(P),C.POINTER(P)])
open_=fn('database_open',[P,C.POINTER(P)]);connect=fn('database_connect',[P,C.POINTER(P),C.POINTER(P)])
prepare=fn('connection_prepare_single',[P,C.c_char_p,C.POINTER(P),C.POINTER(P)])
step=fn('statement_step',[P,C.POINTER(P)]);io=fn('statement_run_io',[P,C.POINTER(P)])
count=fn('statement_column_count',[P],C.c_int64);kind=fn('statement_row_value_kind',[P,C.c_size_t]);integer=fn('statement_row_value_int',[P,C.c_size_t],C.c_int64)
ptr=fn('statement_row_value_bytes_ptr',[P,C.c_size_t],P);size=fn('statement_row_value_bytes_count',[P,C.c_size_t],C.c_int64)
finalize=fn('statement_finalize',[P,C.POINTER(P)]);deinit=fn('statement_deinit',[P],None)
pos=fn('statement_named_position',[P,C.c_char_p],C.c_int64);bint=fn('statement_bind_positional_int',[P,C.c_size_t,C.c_int64]);btext=fn('statement_bind_positional_text',[P,C.c_size_t,C.c_char_p,C.c_size_t])
def check(s,e):
 if s not in (0,1,2,3):raise RuntimeError((s,C.string_at(e.value) if e.value else None))
e=P();db=P();conn=P()
def open_database(path):
 global db,conn
 cfg=Config(path=str(path).encode());check(new(C.byref(cfg),C.byref(db),C.byref(e)),e);check(open_(db,C.byref(e)),e);check(connect(db,C.byref(conn),C.byref(e)),e)
def query(sql,params={}):
 st=P();check(prepare(conn,sql.encode(),C.byref(st),C.byref(e)),e)
 try:
  for k,v in params.items():
   i=pos(st,k.encode())
   if i<=0:continue
   check(bint(st,i,v) if isinstance(v,int) else btext(st,i,v.encode(),len(v.encode())),e)
  rows=[]
  while True:
   s=step(st,C.byref(e));check(s,e)
   if s==1:return rows
   if s==3:check(io(st,C.byref(e)),e);continue
   if s==2:
    row=[]
    for i in range(count(st)):
     k=kind(st,i);row.append(integer(st,i) if k==1 else C.string_at(ptr(st,i),size(st,i)).decode() if k==3 else None)
    rows.append(row)
 finally:finalize(st,C.byref(e));deinit(st)

# Synthetic native key-page diagnostic; the full rebuild benchmark is separate.
import argparse, hashlib, heapq, json, platform, resource, shutil, subprocess, sys, tempfile, time
BASE='bucket > $bucket OR (bucket = $bucket AND ($resume = 0 OR collection > $collection OR (collection = $collection AND id > $id)))'
PREDICATES={'A':BASE,'B':'bucket >= $bucket AND ('+BASE+')'}
SCHEMA=[
 'CREATE TABLE _tb_state (collection TEXT NOT NULL, id TEXT NOT NULL, bucket INTEGER NOT NULL, state TEXT NOT NULL, stamp INTEGER NOT NULL, origin INTEGER NOT NULL, live INTEGER NOT NULL, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL, UNIQUE (collection,id));',
 'CREATE INDEX _tb_state_bucket ON _tb_state(bucket,collection,id);',
 'CREATE INDEX _tb_state_dead ON _tb_state(stamp) WHERE live=0;',
]
# Valid document-key UTF-8 boundaries, before/after the numeric synthetic keys.
SPECIAL=[(0,'items','!first'),(0,'items','é'),(0,'items','\ue000'),(0,'items','\U00010000'),
         (2048,'items','!middle'),(4095,'items','!last-bucket'),(4095,'items','\U0010ffff')]
PREFLIGHT='''SELECT coalesce(sum(collection_bytes + id_bytes),0), count(*),
 coalesce(min(collection_bytes),1),coalesce(max(collection_bytes),0),
 coalesce(min(id_bytes),1),coalesce(max(id_bytes),0),
 count(*)-count(collection_bytes),count(*)-count(id_bytes),
 coalesce(min(valid_hash),1),coalesce(min(valid_bucket),1)
 FROM (SELECT octet_length(collection) AS collection_bytes,octet_length(id) AS id_bytes,
 typeof(h1)='integer' AND typeof(h2)='integer' AS valid_hash,
 typeof(bucket)='integer' AND bucket>=0 AND bucket<4096 AS valid_bucket
 FROM _tb_state WHERE {predicate} ORDER BY bucket,collection,id LIMIT $limit);'''
PROJECTION='SELECT bucket,collection,id,h1,h2 FROM _tb_state WHERE {predicate} ORDER BY bucket,collection,id LIMIT $limit;'

def regular_key(i):return (i%4096,'items',f'{i:026d}')
def seed_keys(n):
 for i in range(n-len(SPECIAL)):yield regular_key(i)
 yield from SPECIAL

def expected_keys(n):
 # Generate the oracle directly in index order, without holding the dataset in Python.
 for bucket in range(4096):
  regular=(regular_key(i) for i in range(bucket,n-len(SPECIAL),4096))
  special=sorted(k for k in SPECIAL if k[0]==bucket)
  yield from heapq.merge(regular,special)

def update_digest(digest,key):
 for value in (str(key[0]),key[1],key[2]):
  data=value.encode();digest.update(len(data).to_bytes(4,'big'));digest.update(data)

def oracle(n):
 d=hashlib.sha256();count_=0
 for key in expected_keys(n):update_digest(d,key);count_+=1
 return {'count':count_,'digest':d.hexdigest()}

def seed(path,n):
 open_database(path)
 for sql in SCHEMA:query(sql)
 # Prepared insertion; source pages and seed are never retained in Python.
 reset=fn('statement_reset',[P,C.POINTER(P)])
 sql='INSERT INTO _tb_state(collection,id,bucket,state,stamp,origin,live,h1,h2) VALUES($collection,$id,$bucket,\'{}\',1,1,1,1,2);'
 st=P();check(prepare(conn,sql.encode(),C.byref(st),C.byref(e)),e)
 positions={k:pos(st,k.encode()) for k in ('$collection','$id','$bucket')}
 try:
  query('BEGIN;')
  for bucket,collection,id_ in seed_keys(n):
   check(bint(st,positions['$bucket'],bucket),e)
   for k,value in (('$collection',collection),('$id',id_)):
    data=value.encode();check(btext(st,positions[k],data,len(data)),e)
   while True:
    status=step(st,C.byref(e));check(status,e)
    if status==1:break
    if status==3:check(io(st,C.byref(e)),e)
   check(reset(st,C.byref(e)),e)
  query('COMMIT;')
 finally:finalize(st,C.byref(e));deinit(st)
 # Checkpoint and close explicitly before copying the main file; process exit alone is insufficient.
 query('PRAGMA wal_checkpoint(TRUNCATE);')
 close=fn('connection_close',[P,C.POINTER(P)])
 check(close(conn,C.byref(e)),e)
 fn('connection_deinit',[P],None)(conn)
 fn('database_deinit',[P],None)(db)
 print(json.dumps({'seed_count':n}))

def page(variant,cursor=None):
 bucket,collection,id_=cursor if cursor else (0,'','')
 params={'$bucket':bucket,'$resume':int(cursor is not None),'$collection':collection,'$id':id_,'$limit':1025}
 predicate=PREDICATES[variant]
 preflight=query(PREFLIGHT.format(predicate=predicate),params)[0]
 assert preflight[8:10]==[1,1] and preflight[6:8]==[0,0]
 keys=query(PROJECTION.format(predicate=predicate),params)
 assert preflight[1]==len(keys) and len(keys)<=1025
 return keys

CURSORS=[None,(2048,'items',''),(4095,'items','')]

def full_walk_check(variant,n):
 # Small correctness dataset only. Never used as a timed large-data benchmark.
 digest=hashlib.sha256();cursor=None;previous=None;count_=0
 while True:
  rows=page(variant,cursor)
  for row in rows[:1024]:
   key=tuple(row[:3]);assert previous is None or key>previous,(previous,key)
   update_digest(digest,key);previous=key;count_+=1
  if len(rows)<=1024:break
  cursor=tuple(rows[1023][:3]);del rows
 assert {'count':count_,'digest':digest.hexdigest()}==oracle(n)

def sample_oracles(n):
 # One arithmetic stream, three bounded digest/count accumulators; outside timing.
 digests=[hashlib.sha256() for _ in CURSORS];counts=[0]*len(CURSORS)
 for key in expected_keys(n):
  for index,cursor in enumerate(CURSORS):
   if counts[index]<1025 and (cursor is None or key>cursor):
    update_digest(digests[index],key);counts[index]+=1
 return [{'count':count_,'digest':digest.hexdigest()} for count_,digest in zip(counts,digests)]

def file_hash(path):
 with open(path,'rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()

def sample(path,variant,n):
 expected=sample_oracles(n)
 metadata={'script_sha256':file_hash(__file__),'native_sha256':file_hash(libpath),
  'native_library':libpath,'native_version':'0.8.2','python':sys.version,'platform':platform.platform(),
  'source_variant':variant,'predicate':PREDICATES[variant],
  'schema_sha256':hashlib.sha256('\n'.join(SCHEMA).encode()).hexdigest(),
  'query_sha256':hashlib.sha256((PREFLIGHT+PROJECTION).encode()).hexdigest(),
  'sample_scope':'three independent key-page requests;1025 projected keys include one lookahead;no traversal/rebuild throughput or fullcoverage proof'}
 open_database(path)
 before_rss=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
 samples=[]
 for cursor,wanted in zip(CURSORS,expected):
  started=time.perf_counter();rows=page(variant,cursor);elapsed=time.perf_counter()-started
  digest=hashlib.sha256();previous=None
  for row in rows:
   key=tuple(row[:3]);assert previous is None or key>previous,(previous,key)
   update_digest(digest,key);previous=key
  actual={'count':len(rows),'digest':digest.hexdigest()}
  assert actual==wanted,(cursor,actual,wanted)
  samples.append({'cursor':cursor,'seconds':elapsed,**actual})
  del rows
 result={'variant':variant,'n':n,'samples':samples,
  'three_request_seconds':sum(row['seconds'] for row in samples),
  'peak_rss_bytes':resource.getrusage(resource.RUSAGE_SELF).ru_maxrss*1024,
  'rss_highwater_before_sampling_bytes':before_rss*1024,
  'memory_scope':'absolute Linux child ru_maxrss;includes oracle/interpreter/native cache/sorter/page objects;not isolated allocation delta;excludes seed process',
  **metadata}
 print(json.dumps(result))

def selfcheck(path,n=4103):
 open_database(path)
 # Cursor starts, continuation in one bucket, transition to next nonempty bucket,
 # final bucket, and UTF-8 ordering around BMP/supplementary key boundaries.
 cursors=[None,(0,'items','00000000000000000000000000'),(0,'items','\ue000'),
          (0,'items','\U00010000'),(2048,'items','!middle'),(4095,'items','\U0010ffff')]
 for cursor in cursors:
  a=page('A',cursor);b=page('B',cursor)
  wanted=[(*k,1,2) for k in expected_keys(n) if cursor is None or k>cursor][:1025]
  assert [tuple(k) for k in a]==wanted and a==b,(cursor,a,b)
 for variant in ('A','B'):full_walk_check(variant,n)
 print(json.dumps({'selfcheck':'passed','checkpoints':len(cursors),'full_walk_variants':['A','B'],'n':n}))

def child(*args):
 completed=subprocess.run([sys.executable,__file__,*map(str,args)],capture_output=True,text=True)
 if completed.returncode:
  print(completed.stderr,file=sys.stderr);completed.check_returncode()
 return json.loads(completed.stdout)

def main():
 parser=argparse.ArgumentParser(description='Turso sampled key-page diagnostic: explicit --run required.')
 parser.add_argument('--run',action='store_true');parser.add_argument('--selfcheck',action='store_true')
 parser.add_argument('--output',type=Path);parser.add_argument('--worker',choices=['seed','sample','check'])
 parser.add_argument('--database');parser.add_argument('--n',type=int);parser.add_argument('--variant',choices=['A','B'])
 args=parser.parse_args()
 if args.worker=='seed':seed(args.database,args.n);return
 if args.worker=='sample':sample(args.database,args.variant,args.n);return
 if args.worker=='check':selfcheck(args.database,args.n);return
 if not args.run and not args.selfcheck:
  parser.print_help();print('No work performed. Planned sizes100000/1000000,three page requests perworker,three alternating A/Bpairs per size.');return
 if args.run and args.output is None:parser.error('--run requires --output JSONL path')
 with tempfile.TemporaryDirectory(prefix='turso-paging-synthetic-') as temporary:
  root=Path(temporary)
  check_db=root/'check.db';child('--worker','seed','--database',check_db,'--n',4103)
  print(child('--worker','check','--database',check_db,'--n',4103))
  if not args.run:return
  with args.output.open('x') as out:
   for n in (100000,1000000):
    seed_db=root/f'seed-{n}.db';seed_result=child('--worker','seed','--database',seed_db,'--n',n)
    assert not Path(str(seed_db)+'-wal').exists() or Path(str(seed_db)+'-wal').stat().st_size==0
    for repeat in range(1,4):
     order=('A','B') if repeat%2 else ('B','A')
     for variant in order:
      # Identical fresh synthetic state per comparison, fresh native cache per worker.
      run_db=root/f'run-{n}-{repeat}-{variant}.db';shutil.copyfile(seed_db,run_db)
      result=child('--worker','sample','--database',run_db,'--n',n,'--variant',variant)
      result.update(repeat=repeat,order=''.join(order),native_library=libpath,
       scope='sampled key-page microbenchmark; NOT traversal/rebuild throughput or fullcoverage proof',
       storage='fresh identical copied synthetic persisted database; OS page cache uncontrolled')
      out.write(json.dumps(result)+'\n');out.flush();print(json.dumps(result),flush=True)
      run_db.unlink()
if __name__=='__main__':main()
