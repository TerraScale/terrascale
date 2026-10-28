"""Record owned and external process CPU without reading command arguments."""
import datetime
import glob
import json
import os
import sys
import time

root = int(sys.argv[1])
hz = os.sysconf('SC_CLK_TCK')


def snapshot():
    result = {}
    for path in glob.glob('/proc/[0-9]*/stat'):
        try:
            with open(path) as source:
                raw = source.read()
            fields = raw.rsplit(')', 1)[1].split()
            result[int(path.split('/')[2])] = (int(fields[1]), int(fields[11]) + int(fields[12]), int(fields[19]), raw.split('(', 1)[1].rsplit(')', 1)[0])
        except (OSError, ValueError, IndexError):
            pass
    return result


def owned(pid, rows):
    seen = set()
    while pid not in seen and pid in rows:
        if pid == root:
            return True
        seen.add(pid)
        pid = rows[pid][0]
    return False


before, start = snapshot(), time.monotonic()
while True:
    time.sleep(1)
    after, now = snapshot(), time.monotonic()
    own, external = 0., []
    for pid, row in after.items():
        old = before.get(pid)
        if old is None or old[2] != row[2]:
            continue
        cpu = max(0, row[1] - old[1]) * 100 / hz / (now - start)
        if owned(pid, after):
            own += cpu
        elif cpu > 0:
            external.append((round(cpu, 2), pid, row[3]))
    external.sort(reverse=True)
    print(json.dumps(dict(utc=datetime.datetime.now(datetime.timezone.utc).isoformat(), owned_cpu=round(own, 2), external_cpu=round(sum(v[0] for v in external), 2), external_top=external[:5])), flush=True)
    before, start = after, now
