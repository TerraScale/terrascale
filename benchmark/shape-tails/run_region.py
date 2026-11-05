import json,pathlib,subprocess,sys
import account_database as p
region=sys.argv[1]
r=subprocess.run(['ssh','-o','BatchMode=yes','ts-'+region+'-1','python3','-c',__import__('shlex').quote(pathlib.Path(__file__).with_name('regional_client.py').read_text())],input=json.dumps({'bearer':p.bearer,'project':p.state['projectId'],'region':region}),text=True,capture_output=True,timeout=240)
print(r.stdout,end='')
if r.returncode:
 print(r.stderr,file=sys.stderr)
 sys.exit(r.returncode)
