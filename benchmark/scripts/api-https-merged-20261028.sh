#!/usr/bin/env bash
# Frozen-source HTTPS control; run through ts-heavy-any nice -n 19.
cd -- "$(dirname -- "$0")/../.."
set -euo pipefail
sdk=$HOME/remote-builds/ts-dotnet/https-merged-0018
mkdir -p "$(dirname "$sdk")"
test ! -e "$sdk"
git clone --quiet --no-checkout https://github.com/TerraScale/ts-dotnet.git "$sdk"
git -C "$sdk" checkout --quiet --detach 36d114877a69d18fba33b27ad362bd3e68e9e7ff
out=$PWD/benchmark/bench/https-merged-0018-quiet-2
mkdir "$out"
python3 - "$out" <<'PY'
from pathlib import Path
import sys
source=Path('benchmark/dotnet/VsTurso/run-https.sh').read_text()
source=source.replace('src/TerraScale.DotNet/AuthService/TerraScaleAuthClient.cs)', 'src/TerraScale.DotNet/AuthService/TerraScaleAuthClient.cs src/TerraScale.DotNet/TerraScale.DotNet.csproj)')
hook='''
# Builds are complete. Reserve other admitted heavy slots without stopping work.
reserve_fds=()
for slot in 1 2 3 4; do
  exec {reserve_fd}>"/tmp/ts-heavy-$slot.lock"
  if flock -n "$reserve_fd"; then reserve_fds+=("$reserve_fd"); fi
done
if [[ ${#reserve_fds[@]} != 3 ]]; then
  echo 'Quiet reservation unavailable; no timed arm started.' >&2; exit 73
fi
out=$(dirname "$prefix")
sha256sum "$bin/before/TerraBase.E2E.dll" "$bin/after/TerraBase.E2E.dll" "$bin/before/TerraScale.DotNet.dll" "$bin/after/TerraScale.DotNet.dll" > "$out/artifacts.sha256"
python3 -u "$out/cpu-monitor.py" "$$" > "$out/cpu-processes.jsonl" &
observer=$!
trap 'kill "$observer" 2>/dev/null || true; restore' EXIT
'''
source=source.replace('for repeat in 1 2 3; do',hook+'\nfor repeat in 1 2 3; do')
Path('benchmark/dotnet/VsTurso/run-https-merged.sh').write_text(source)
PY
cat > "$out/cpu-monitor.py" <<'PY'
import glob,time,os,json,datetime,sys
root=int(sys.argv[1]);hz=os.sysconf('SC_CLK_TCK')
def snapshot():
 result={}
 for path in glob.glob('/proc/[0-9]*/stat'):
  try:
   raw=open(path).read();a=raw.rsplit(')',1)[1].split()
   result[int(path.split('/')[2])]=(int(a[1]),int(a[11])+int(a[12]),int(a[19]),raw.split('(',1)[1].rsplit(')',1)[0])
  except (OSError,ValueError,IndexError):pass
 return result
def owned(pid,rows):
 seen=set()
 while pid not in seen and pid in rows:
  if pid==root:return True
  seen.add(pid);pid=rows[pid][0]
 return False
before=snapshot();start=time.monotonic()
while True:
 time.sleep(1);after=snapshot();now=time.monotonic();elapsed=now-start;own=0.;external=[]
 for pid,row in after.items():
  old=before.get(pid)
  if old is None or old[2]!=row[2]:continue
  cpu=max(0,row[1]-old[1])*100/hz/elapsed
  if owned(pid,after):own+=cpu
  elif cpu>0:external.append((round(cpu,2),pid,row[3]))
 external.sort(reverse=True)
 print(json.dumps(dict(utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),owned_cpu=round(own,2),external_cpu=round(sum(v[0] for v in external),2),external_top=external[:5])),flush=True)
 before=after;start=now
PY
# Reject the quiet label if external mean exceeds 25% of one CPU, p95 exceeds 100%,
# or another compiler/test consumes >=10%; retain all artifacts in either case.
bash benchmark/dotnet/VsTurso/run-https-merged.sh "$sdk" fba075bcba060afb560c711b395385626df91161 "$out/control" > "$out/run.log" 2>&1 || { tail -65 "$out/run.log";exit 1; }
python3 - "$out" <<'PY'
import json,statistics,sys
from pathlib import Path
p=Path(sys.argv[1]); rows=[json.loads(s) for s in (p/'cpu-processes.jsonl').read_text().splitlines()]
if not rows:raise SystemExit('Missing CPU evidence')
x=sorted(r['external_cpu'] for r in rows); p95=x[min(len(x)-1,int(.95*len(x)))];mean=statistics.mean(x)
result=dict(samples=len(x),external_mean=mean,external_p95=p95,external_max=max(x),quiet_cpu_thresholds=mean<=25 and p95<=100)
(p/'quiet-summary.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
for f in sorted(p.glob('control*.md')):print(f.name)
PY
