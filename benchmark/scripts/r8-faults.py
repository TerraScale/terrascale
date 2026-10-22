#!/usr/bin/env python3
"""Run independent Slow seeds on a dev host; preserve every failed seed's log."""
import concurrent.futures
import datetime
import hashlib
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import threading
import time
import xml.etree.ElementTree as ET

first = int(sys.argv[1]) if len(sys.argv) > 1 else 0
count = int(sys.argv[2]) if len(sys.argv) > 2 else 200
if first < 0 or count < 1 or first + count > 2**31 - 1:
    raise SystemExit("usage: r8-faults.py [first nonnegative seed] [positive count]")
root = Path(__file__).resolve().parents[2]


def source_identity():
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    dirty = subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=all", "--", ".",
                                     ":(exclude)benchmark/bench/r8-faults-*/**"], cwd=root, text=True)
    if dirty:
        raise SystemExit("R8 requires a clean checkout; commit source changes before qualification")
    return sha


sha = source_identity()
stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
output = root / "benchmark" / "bench" / f"r8-faults-{stamp}"
output.mkdir()
print(f"R8 host={socket.gethostname()} sha={sha} seeds={first}..{first+count-1} logs={output}", flush=True)
with (output / "build.log").open("w") as stream:
    build = subprocess.run(["nice", "-n", "19", "dotnet", "build", "tests/TerraBase.Slow", "-c", "Release"],
                           cwd=root, stdout=stream, stderr=subprocess.STDOUT)
if build.returncode:
    raise SystemExit(f"R8 build failed; no seeds executed; see {output / 'build.log'}")
if source_identity() != sha:
    raise SystemExit("R8 source changed during build")
binary = root / "tests/TerraBase.Slow/bin/Release/net11.0/TerraBase.Slow.dll"
(output / "binary.sha256").write_text(hashlib.sha256(binary.read_bytes()).hexdigest() + "\n")
active = {}
gate = threading.Lock()
stopping = threading.Event()


def kill(process):
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass


def interrupt(_signal, _frame):
    raise KeyboardInterrupt


signal.signal(signal.SIGTERM, interrupt)


def run(seed):
    start = time.monotonic()
    log = output / f"seed-{seed}.log"
    env = dict(os.environ, R8_SEED=str(seed), R8_COUNT="1")
    command = ["nice", "-n", "19", "dotnet", "run", "--project", "tests/TerraBase.Slow",
               "--configuration", "Release", "--no-build", "--", "--treenode-filter",
               "/*/*/RandomizedReplicationFaultTests/*", "--minimum-expected-tests", "1",
               "--output", "detailed", "--no-ansi", "--report-trx", "--results-directory", str(output / str(seed))]
    with log.open("w") as stream:
        with gate:
            if stopping.is_set():
                raise RuntimeError("R8 run cancelled before seed launch")
            process = subprocess.Popen(command, cwd=root, env=env, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
            active[process.pid] = process
        try:
            code = process.wait(timeout=900)
        except subprocess.TimeoutExpired:
            kill(process)
            code = process.wait()
            stream.write("\nR8 runner: seed exceeded 900 seconds; killed its process group\n")
        finally:
            kill(process)
            with gate:
                del active[process.pid]
    reports = list((output / str(seed)).glob("*.trx"))
    try:
        rows = ET.parse(reports[0]).findall(".//{*}UnitTestResult") if len(reports) == 1 else []
    except (ET.ParseError, OSError):
        rows = []
    passed = len(rows) == 1 and rows[0].get("outcome") == "Passed" and rows[0].get("testName") == f"Acknowledged_writes_converge_through_seeded_faults({seed})"
    status = "PASS" if code == 0 and passed else "FAIL"
    history = log.read_text().splitlines()
    faults = "all" if passed else ", ".join([label for marker, label in [("clock-skew ", "clock-skew"),
        ("partition ", "partition/delay"), ("loss resets=", "TCP resets"),
        ("SIGKILL ", "SIGKILL"), ("restart ", "restart"), ("slow-fsync ", "slow-fsync")]
        if any(line.lstrip().startswith(marker) for line in history)])
    seconds = round(time.monotonic() - start, 2)
    print(f"R8 {status} seed={seed} seconds={seconds} log={log}", flush=True)
    return seed, status, faults, seconds, log.name


with (output / "results.md").open("w") as table:
    table.write(f"Host: `{socket.gethostname()}`; commit: `{sha}`; UTC: `{stamp}`.\n\n")
    table.write("| Seed | Result | Faults reached | Seconds | Log |\n|---:|---|---|---:|---|\n")
    failed = 0
    # Four independent test processes bound host resource use; each seed owns its nodes and ports.
    pool = concurrent.futures.ThreadPoolExecutor(max_workers=4)
    try:
        for seed, status, faults, seconds, log in pool.map(run, range(first, first + count)):
            table.write(f"| {seed} | {status} | {faults} | {seconds} | [{log}]({log}) |\n")
            table.flush()
            failed += status == "FAIL"
    finally:
        stopping.set()
        with gate:
            for process in active.values():
                kill(process)
        pool.shutdown(wait=True, cancel_futures=True)
if source_identity() != sha:
    raise SystemExit("R8 source changed during qualification; discard results")
print(f"R8 finished seeds={count} failed={failed} table={output / 'results.md'}", flush=True)
raise SystemExit(bool(failed))
