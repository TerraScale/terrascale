#!/usr/bin/env python3
"""Reserve the dev host's four ts-heavy slots for the entire measured suite."""
import contextlib
import datetime
import fcntl
import json
import os
import pathlib
import signal
import subprocess
import sys
import time

pid, own = os.getppid(), None
while pid > 1:
    process = pathlib.Path('/proc', str(pid))
    args = process.joinpath('cmdline').read_bytes().decode().split('\0')
    if pathlib.Path(args[0]).name == 'flock':
        own = next((pathlib.Path(arg) for arg in args if pathlib.Path(arg).name.startswith('ts-heavy-') and arg.endswith('.lock')), None)
        if own:
            break
    pid = int(process.joinpath('stat').read_text().rsplit(')', 1)[1].split()[1])
if own is None:
    sys.exit('run the performance suite through ts-heavy-any')

with contextlib.ExitStack() as reservations:
    locks = [own.parent / f'ts-heavy-{slot}.lock' for slot in range(1, 5)]
    for path in locks:
        if path == own:
            continue
        stream = reservations.enter_context(path.open('a'))
        try:
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            sys.exit('host has another heavy job; retry on a quiet host')
    observations, deadline = [], time.monotonic() + 300
    while True:
        load = os.getloadavg()
        pressure = pathlib.Path('/proc/pressure/cpu').read_text()
        observations.append(dict(at=time.time(), load=load, cpu_pressure=pressure))
        if load[0] <= os.cpu_count() / 4 and float(pressure.split('avg10=')[1].split()[0]) <= 1:
            break
        if time.monotonic() >= deadline:
            sys.exit('reserved host did not become quiet within five minutes')
        time.sleep(5)
    output = pathlib.Path(sys.argv[1])
    output.mkdir()
    output.joinpath('admission.json').write_text(json.dumps(dict(
        at=datetime.datetime.now(datetime.timezone.utc).isoformat(), host=os.uname().nodename,
        scheduler_locks=[str(path) for path in locks], load=load, cpu_pressure=pressure,
        admission_observations=observations)) + '\n')
    child, pending = None, None

    def stop(signum, _):
        global pending
        pending = signum
        for value in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
            signal.signal(value, signal.SIG_IGN)
        if child is not None:
            child.send_signal(signum)

    for value in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        signal.signal(value, stop)
    child = subprocess.Popen(['bash', 'benchmark/scripts/perf-local.sh', str(output)])
    if pending is not None:
        child.send_signal(pending)
    sys.exit(child.wait())
