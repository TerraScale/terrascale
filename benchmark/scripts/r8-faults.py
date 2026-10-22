#!/usr/bin/env python3
"""Run independent Slow seeds on a dev host; preserve every failed seed's log."""
import concurrent.futures
import datetime
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

first = int(sys.argv[1]) if len(sys.argv) > 1 else 0
count = int(sys.argv[2]) if len(sys.argv) > 2 else 200
if first < 0 or count < 1 or first + count > 2**31 - 1:
    raise SystemExit("usage: r8-faults.py [first nonnegative seed] [positive count]")
root = Path(__file__).resolve().parents[2]
stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
output = root / "benchmark" / "bench" / f"r8-faults-{stamp}"
output.mkdir()
sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
print(f"R8 host={socket.gethostname()} sha={sha} seeds={first}..{first+count-1} logs={output}", flush=True)


def run(seed):
    start = time.monotonic()
    log = output / f"seed-{seed}.log"
    env = dict(os.environ, R8_SEED=str(seed), R8_COUNT="1")
    command = ["nice", "-n", "19", "dotnet", "run", "--project", "tests/TerraBase.Slow",
               "--configuration", "Release", "--no-build", "--", "--treenode-filter",
               "/*/*/RandomizedReplicationFaultTests/*", "--minimum-expected-tests", "1",
               "--output", "detailed", "--no-ansi", "--report-trx", "--results-directory", str(output / str(seed))]
    with log.open("w") as stream:
        process = subprocess.Popen(command, cwd=root, env=env, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            code = process.wait(timeout=900)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            code = process.wait()
            stream.write("\nR8 runner: seed exceeded 900 seconds; killed its process group\n")
    reports = list((output / str(seed)).glob("*.trx"))
    rows = ET.parse(reports[0]).findall(".//{*}UnitTestResult") if len(reports) == 1 else []
    passed = len(rows) == 1 and rows[0].get("outcome") == "Passed" and rows[0].get("testName") == f"Acknowledged_writes_converge_through_seeded_faults({seed})"
    status = "PASS" if code == 0 and passed else "FAIL"
    seconds = round(time.monotonic() - start, 2)
    print(f"R8 {status} seed={seed} seconds={seconds} log={log}", flush=True)
    return seed, status, seconds, log.name


with (output / "results.md").open("w") as table:
    table.write(f"Host: `{socket.gethostname()}`; commit: `{sha}`; UTC: `{stamp}`.\n\n")
    table.write("| Seed | Result | Seconds | Log |\n|---:|---|---:|---|\n")
    failed = 0
    # Four independent test processes bound host resource use; each seed owns its nodes and ports.
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        for seed, status, seconds, log in pool.map(run, range(first, first + count)):
            table.write(f"| {seed} | {status} | {seconds} | [{log}]({log}) |\n")
            table.flush()
            failed += status == "FAIL"
print(f"R8 finished seeds={count} failed={failed} table={output / 'results.md'}", flush=True)
raise SystemExit(bool(failed))
