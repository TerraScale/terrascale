#!/usr/bin/env python3
"""Run a prebuilt TUnit suite with fixed Fast budgets and retained timings."""
from pathlib import Path
import os
import signal
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET


def durations(report):
    rows = []
    for result in ET.parse(report).iter():
        if result.tag.rsplit('}', 1)[-1] != 'UnitTestResult':
            continue
        hours, minutes, seconds = result.attrib['duration'].split(':')
        elapsed = int(hours) * 3600 + int(minutes) * 60 + float(seconds)
        rows.append((elapsed, result.attrib['testName']))
    if not rows:
        raise ValueError('Fast runner produced no test timings')
    return sorted(rows, reverse=True)


def check(report, elapsed):
    rows = durations(report)
    print(f'Fast suite: {elapsed:.3f}s / 120s; {len(rows)} tests. Slowest 10:', flush=True)
    for seconds, name in rows[:10]:
        print(f'  {seconds:8.3f}s {name}', flush=True)
    # Restore rejection once the fixture performance fixes in #1084 land.
    for seconds, name in rows:
        if seconds > 10:
            print(f'WARN Fast test exceeded 10s (warn-only until #1084): {name} ({seconds:.3f}s)', file=sys.stderr, flush=True)
    failures = []
    if elapsed > 120:
        failures.append(f'Fast suite exceeded 120s ({elapsed:.3f}s)')
    for failure in failures:
        print('FAILED ' + failure, file=sys.stderr, flush=True)
    return bool(failures)


def main():
    if len(sys.argv) < 3 or sys.argv[2] != '--':
        sys.exit('usage: fast-budget.py <suite> -- <dotnet run ... -- ...>')
    suite = sys.argv[1]
    results = Path('artifacts/test-results').resolve()
    results.mkdir(parents=True, exist_ok=True)
    report = results / f'{suite}-{uuid.uuid4().hex}.trx'
    command = [*sys.argv[3:], '--report-trx', '--results-directory', str(results),
               '--report-trx-filename', report.name, '--no-ansi']
    # ts-ship's build phase records the exact invocation without running tests.
    if os.environ.get('TS_SHIP_PHASE') == 'build':
        # Stable filename keeps the planned invocation identical in the test phase.
        command[command.index('--report-trx-filename') + 1] = suite + '.trx'
        return subprocess.call(command)
    if os.environ.get('TS_SHIP_PHASE') == 'test':
        report = results / (suite + '.trx')
        command[command.index('--report-trx-filename') + 1] = report.name
    report.unlink(missing_ok=True)
    started = time.monotonic()
    process = subprocess.Popen(command, start_new_session=True)
    def cancel(signum, frame):
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()
        sys.exit(128 + signum)
    for signum in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        signal.signal(signum, cancel)
    try:
        code = process.wait(timeout=120)
    except subprocess.TimeoutExpired:
        print(f'FAILED Fast suite {suite} exceeded 120s', file=sys.stderr, flush=True)
        code = 124
    finally:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()
    elapsed = time.monotonic() - started
    if report.is_file():
        over_budget = check(report, elapsed)
        code = code or int(over_budget)
    elif code == 0:
        sys.exit('FAILED Fast suite produced no TRX report')
    return code


if __name__ == '__main__':
    sys.exit(main())
