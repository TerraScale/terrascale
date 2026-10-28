# Profiles small c=1 and c=16 read cells of one owned VsTurso process: an untraced /proc interval, then one managed CPU
# EventPipe trace and one allocation-tick trace inside each 45 s measurement window. Usage: OUT PID CELLS DOTNET_TRACE
import json, os, subprocess, sys, time, datetime
out, pid, cells, tool = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
WARMUP, WINDOW = 10.0, 45.0
# Sample profiler (all managed threads, ~1 ms) plus GC, loader, JIT, contention, exception and thread-pool events at
# informational level, and 1 s counters. Hosting at level 1 keeps its per-request events off; only counters flow.
COUNTERS = 'System.Runtime:0:1:EventCounterIntervalSec=1,Microsoft.AspNetCore.Hosting:0:1:EventCounterIntervalSec=1'
CPU = 'Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x1C019:4,' + COUNTERS
# GC verbose adds the ~100 KB allocation tick with its managed stack; loader and JIT keep symbols complete.
ALLOC = 'Microsoft-Windows-DotNETRuntime:0x19:5,' + COUNTERS

def snapshot(tag, log):
    threads = []
    for tid in os.listdir(f'/proc/{pid}/task'):
        base = f'/proc/{pid}/task/{tid}'
        try:
            stat = open(base + '/stat').read()
            fields = stat.rsplit(')', 1)[1].split()
            run_ns, wait_ns, slices = (int(v) for v in open(base + '/schedstat').read().split())
            status = dict(line.split(':', 1) for line in open(base + '/status') if 'ctxt_switches' in line)
            threads.append(dict(tid=int(tid), comm=stat.split('(', 1)[1].rsplit(')', 1)[0], utime=int(fields[11]),
                                stime=int(fields[12]), run_ns=run_ns, wait_ns=wait_ns, slices=slices,
                                vcs=int(status['voluntary_ctxt_switches']), nvcs=int(status['nonvoluntary_ctxt_switches'])))
        except (OSError, ValueError, KeyError):
            pass
    try:
        children = open(f'/proc/{pid}/task/{pid}/children').read().split()
    except OSError:
        children = ['unavailable']
    log.write(json.dumps(dict(tag=tag, mono=time.monotonic(), utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                              children=children, system=open('/proc/stat').readline().split()[1:],
                              pressure=open('/proc/pressure/cpu').read().split('\n')[0], threads=threads)) + '\n')
    log.flush()
    return threads

def cell_lines():
    text = open(cells).read() if os.path.exists(cells) else ''
    return text, sum(1 for line in text.splitlines() if line.startswith('{'))

def wait_for(predicate, limit):
    until = time.monotonic() + limit
    while time.monotonic() < until:
        os.kill(pid, 0)
        if predicate():
            return time.monotonic()
        time.sleep(0.05)
    raise SystemExit('The owned benchmark did not reach the expected cell.')

def trace(name, providers, seconds, cell_end):
    path = f'{out}/{name}.nettrace'
    with open(f'{out}/{name}.collect.log', 'w') as log:
        # Off the benchmark's CPUs (2-11); the in-process EventPipe work still runs inside the owned process.
        rc = subprocess.call(['taskset', '-c', '0-1', tool, 'collect', '-p', str(pid), '--providers', providers,
                              '--duration', f'00:00:00:{seconds:02d}', '--buffersize', '1024', '-o', path],
                             stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
    if rc != 0 or not os.path.exists(path):
        raise SystemExit(f'{name}: dotnet-trace failed with {rc}')
    if time.monotonic() > cell_end:
        raise SystemExit(f'{name}: the trace outlasted its measurement window; reject the profile.')

exe = os.readlink(f'/proc/{pid}/exe')
cmdline = open(f'/proc/{pid}/cmdline').read().split('\0')
if not exe.endswith('/dotnet') or not any(arg.endswith('/TerraBase.E2E.dll') for arg in cmdline):
    raise SystemExit(f'PID {pid} is not the owned benchmark: {exe}')
with open(f'{out}/proc.jsonl', 'w') as log:
    # Cell 0 (small c=1) starts after the preload line; cell 1 (small c=16) after the first result line.
    for index, name in ((0, 'small-c1'), (1, 'small-c16')):
        started = wait_for(lambda: ('# preloaded api' in cell_lines()[0]) if index == 0 else cell_lines()[1] >= 1, 900)
        cell_end = started + WARMUP + WINDOW
        time.sleep(max(0, started + WARMUP + 1 - time.monotonic()))
        snapshot(f'{name}:untraced-start', log)
        time.sleep(5)
        snapshot(f'{name}:untraced-end', log)
        snapshot(f'{name}:cpu-start', log)
        trace(f'{name}-cpu', CPU, 10, cell_end)
        threads = snapshot(f'{name}:cpu-end', log)
        with open(f'{out}/{name}-threads.tsv', 'w') as names:
            names.writelines(f"{thread['tid']}\t{thread['comm']}\n" for thread in threads)
        snapshot(f'{name}:alloc-start', log)
        trace(f'{name}-alloc', ALLOC, 6, cell_end)
        snapshot(f'{name}:alloc-end', log)
        if cell_lines()[1] > index:
            raise SystemExit(f'{name}: the cell finished before tracing did; reject the profile.')
print('Both small cells were profiled inside their measurement windows.')
