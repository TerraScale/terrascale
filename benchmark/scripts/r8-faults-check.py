import hashlib, os, pathlib, shutil, subprocess, sys, tempfile, time
source = pathlib.Path(__file__).with_name("r8-faults.py")
with tempfile.TemporaryDirectory(prefix='r8-runner-check-') as name:
    root=pathlib.Path(name)
    repo=root/'repo'; script=repo/'benchmark/scripts/r8-faults.py'; script.parent.mkdir(parents=True)
    shutil.copyfile(source,script)
    (repo/"benchmark/bench").mkdir(parents=True)
    (repo/'.gitignore').write_text('bin/\n')
    subprocess.run(['git','init','-q',str(repo)],check=True)
    subprocess.run(['git','-C',str(repo),'add','.'],check=True)
    subprocess.run(['git','-C',str(repo),'-c','user.name=R8','-c','user.email=r8@example.test','commit','-qm','runner check'],check=True)
    fake=root/'dotnet'
    fake.write_text('''#!/usr/bin/env python3
import os,pathlib,subprocess,sys,time
if sys.argv[1]=='build':
 p=pathlib.Path('tests/TerraBase.Slow/bin/Release/net11.0/TerraBase.Slow.dll'); p.parent.mkdir(parents=True,exist_ok=True); p.write_bytes(b'new-build'); raise SystemExit(0)
seed=int(os.environ['R8_SEED'])
if os.environ.get('R8_CANCEL_CHECK'):
 child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(300)'])
 pathlib.Path(os.environ['R8_CANCEL_CHECK'],str(seed)).write_text(str(child.pid))
 time.sleep(300)
results=pathlib.Path(sys.argv[sys.argv.index('--results-directory')+1]); results.mkdir(parents=True)
status='Passed' if seed==0 else 'Failed'
(results/'result.trx').write_text(f'<TestRun><Results><UnitTestResult testName="Acknowledged_writes_converge_through_seeded_faults({seed})" outcome="{status}" /></Results></TestRun>')
''')
    fake.chmod(0o700)
    env=dict(os.environ,PATH=str(root)+os.pathsep+os.environ['PATH'])
    first=subprocess.run([sys.executable,str(script),'0','2'],cwd=repo,env=env,capture_output=True,text=True)
    assert first.returncode==1,first.stderr
    output=next((repo/'benchmark/bench').iterdir())
    table=(output/'results.md').read_text()
    assert '| 0 | PASS | all |' in table,table
    assert '| 1 | FAIL |  |' in table,table
    assert (output/'binary.sha256').read_text().strip()==hashlib.sha256(b'new-build').hexdigest()
    script.write_text(script.read_text()+'\n# dirty source\n')
    dirty=subprocess.run([sys.executable,str(script),'0','1'],cwd=repo,env=env,capture_output=True,text=True)
    assert dirty.returncode!=0 and 'clean checkout' in dirty.stderr
    subprocess.run(['git','-C',str(repo),'checkout','--',str(script)],check=True)
    # A distinct second avoids the runner's timestamped output-directory collision.
    time.sleep(1.05)
    control=root/'pids'; control.mkdir()
    env['R8_CANCEL_CHECK']=str(control)
    cancel=subprocess.Popen([sys.executable,str(script),'0','8'],cwd=repo,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    try:
        deadline=time.monotonic()+15
        while len(list(control.iterdir()))<4 and time.monotonic()<deadline:
            time.sleep(.02)
        assert len(list(control.iterdir()))==4,'four active seed sessions were not launched'
        children=[int(p.read_text()) for p in control.iterdir()]
    finally:
        cancel.terminate()
        cancel.communicate(timeout=10)
    assert cancel.returncode!=0
    for pid in children:
        status=pathlib.Path(f'/proc/{pid}/stat')
        assert not status.exists() or status.read_text().split(') ',1)[1].startswith('Z '),f'orphan child {pid}'
    assert len(list(control.iterdir()))==4,'queued seeds launched after cancellation'
    print('PASS: exact TRX outcomes, observed fault labels, fresh binary SHA, dirty-source refusal, cancellation of four seed groups and queued work')
