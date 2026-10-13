import concurrent.futures, json, os, pathlib, signal, socket, subprocess, threading, time, uuid
root=pathlib.Path.cwd(); case=root/'artifacts/f8'/os.environ.get('F8_LABEL','before'); case.mkdir(parents=True,exist_ok=True)
def port():
    with socket.socket() as s: s.bind(('127.0.0.1',0)); return s.getsockname()[1]
def uid(): return '01'+uuid.uuid4().hex[:24].upper().replace('I','A').replace('L','B').replace('O','C').replace('U','D')
public,internal,metrics=port(),port(),port(); env={k:v for k,v in os.environ.items() if not k.startswith(('FLY_','TERRABASE_','AWS_'))}; env['DOTNET_ENVIRONMENT']='Development'
log=open(case/'node.log','w'); process=subprocess.Popen(['dotnet',os.environ.get('F8_BINARY',str(root/'src/TerraBase/bin/Debug/net11.0/terrabase.dll')),f'--data-directory={case}/data',f'--port={public}',f'--internal-port={internal}',f'--metrics-port={metrics}'],env=env,stdout=log,stderr=log)
def request(method,path,body=None):
    cmd=['curl','--silent','--show-error','--max-time','10','--http2-prior-knowledge','-X',method,f'http://127.0.0.1:{internal}'+path,'-H','Content-Type: application/json',]
    if path.startswith('/v1/management/'): cmd+=['-H','X-TerraBase-User-Id: '+owner]
    if body is not None: cmd+=['--data-binary',json.dumps(body)]
    return subprocess.run(cmd,capture_output=True,text=True)
owner,org,project,database=[uid() for _ in range(4)]; streams=[]; stop=threading.Event(); writes=[0]; errors=[]
try:
    until=time.monotonic()+60
    while time.monotonic()<until:
        if process.poll() is not None: raise RuntimeError('node exited at startup')
        result=subprocess.run(['curl','--silent','--fail',f'http://127.0.0.1:{public}/healthz'],capture_output=True)
        if result.returncode==0: break
        time.sleep(.1)
    commit=request('POST','/v1/management/commits',{'id':uid(),'ops':[{'op':'create_organization','id':org,'name':'f8'},{'op':'set_plan','organization':org,'plan':'enterprise'},{'op':'create_project','organization':org,'id':project,'name':'f8'},{'op':'create_database','organization':org,'project':project,'name':'db','id':database}]})
    print('commit',commit.stdout)
    path=f'/v1/projects/{project}/databases/db'; print('collection',request('POST',path+'/collections',{'name':'items'}).stdout)
    for i in range(8):
        out=open(case/f'sse-{i}.log','w'); streams.append((subprocess.Popen(['curl','--silent','--show-error','--no-buffer','--http2-prior-knowledge',f'http://127.0.0.1:{internal}'+path+'/changes?cursor=now'],stdout=out,stderr=out),out))
    peerlog=open(case/'peer.log','w')
    peer=subprocess.Popen(['curl','--verbose','--no-buffer','--http2-prior-knowledge','-X','POST','-H','content-type: application/grpc','-H','te: trailers','--upload-file','-',f'http://127.0.0.1:{internal}/terrabase.v1.Replication/Push'],stdin=subprocess.PIPE,stdout=peerlog,stderr=peerlog)
    streams.append((peer,peerlog))
    peer.stdin.write(bytes([0,0,0,0,32])+b'\x0a'); peer.stdin.flush()
    def load():
        while not stop.is_set():
            result=request('PUT',path+'/collections/items/documents/by-key?id='+uid(),{'n':writes[0],'body':'x'*1024})
            if result.returncode: return
            if 'error' in result.stdout: errors.append(result.stdout[:200]); return
            writes[0]+=1
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures=[pool.submit(load) for _ in range(4)]
        until=time.monotonic()+20
        while writes[0]<100 and time.monotonic()<until and not errors: time.sleep(.01)
        if errors: raise RuntimeError(errors)
        if writes[0]<100: raise RuntimeError('insufficient write load')
        active=sum((case/f'sse-{i}.log').read_text().startswith('event: cursor') for i in range(8))
        if active!=8: raise RuntimeError(f'Only {active} active SSE streams')
        started=time.monotonic(); process.send_signal(signal.SIGTERM); process.wait(timeout=45); elapsed=time.monotonic()-started; stop.set()
    result={'label':case.name,'pid':process.pid,'shutdown_seconds':elapsed,'exit_code':process.returncode,'writes':writes[0],'active_sse':active,'incomplete_peer_frame':True,'hostname':socket.gethostname()}
    (case/'result.json').write_text(json.dumps(result,indent=2)); print(json.dumps(result))
finally:
    stop.set()
    if process.poll() is None: process.kill(); process.wait()
    for stream,out in streams:
        if stream.poll() is None: stream.terminate(); stream.wait()
        out.close()
    log.close()
