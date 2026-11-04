import json
import os
import time
from pathlib import Path

root = Path('/sys/fs/cgroup')
previous = None
identity = None


def process():
    matches = []
    for path in Path('/proc').iterdir():
        if not path.name.isdigit():
            continue
        try:
            arguments = (path / 'cmdline').read_bytes().split(b'\0')
            if any(os.path.basename(value) in (b'terrabase', b'terrabase.dll') for value in arguments[:2]):
                fields = (path / 'stat').read_text().rsplit(')', 1)[1].split()
                matches.append((path, int(fields[19])))
        except OSError:
            continue
    if len(matches) != 1:
        raise ValueError('CPU_PROCESS_NOT_UNIQUE')
    return matches[0]


def capacity(path):
    affinity = len(os.sched_getaffinity(int(path.name)))
    cores = float(affinity)
    line = next((value for value in (path / 'cgroup').read_text().splitlines() if value.startswith('0::')), None)
    if line is None:
        raise ValueError('CPU_CGROUP_UNAVAILABLE')
    group = root / line[3:].lstrip('/')
    group.relative_to(root)
    while group != root:
        quota, period = (group / 'cpu.max').read_text().split()
        if quota != 'max':
            if int(quota) <= 0 or int(period) <= 0:
                raise ValueError('CPU_CAPACITY_INVALID')
            cores = min(cores, int(quota) / int(period))
        group = group.parent
    if cores <= 0:
        raise ValueError('CPU_CAPACITY_INVALID')
    return cores, affinity


while True:
    try:
        if identity is None:
            identity = process()
        path, start = identity
        fields = (path / 'stat').read_text().rsplit(')', 1)[1].split()
        if int(fields[19]) != start:
            raise ValueError('CPU_PROCESS_CHANGED')
        cores, affinity = capacity(path)
        used = (int(fields[11]) + int(fields[12])) / os.sysconf('SC_CLK_TCK')
        now = time.monotonic()
        if previous is not None:
            elapsed, delta = now - previous[0], used - previous[1]
            if delta < 0 or cores != previous[2]:
                raise ValueError('CPU_PROCESS_OR_CAPACITY_CHANGED')
            print(json.dumps({'at': time.time(), 'cores': cores, 'affinity_cpus': affinity,
                              'cpu_percent': 100 * delta / elapsed / cores, 'core_percent': 100 * delta / elapsed,
                              'pid': int(path.name), 'start_ticks': start, 'source': 'proc_cgroup_v2'}), flush=True)
        previous = now, used, cores
    except (OSError, ValueError) as error:
        print(json.dumps({'error': 'CPU process or capacity unavailable or changed',
                          'reason': str(error) if isinstance(error, ValueError) else type(error).__name__}), flush=True)
        break
    time.sleep(1)
