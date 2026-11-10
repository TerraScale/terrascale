#!/usr/bin/env python3
"""Run a prebuilt Fast suite with fixed budgets and retained per-test timings."""
from pathlib import Path
import json
import os
import re
import signal
import subprocess
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET

BUDGET_SECONDS = 30
TEST_SECONDS = 10
KILL_SECONDS = 4 * BUDGET_SECONDS
ROOT = Path(__file__).resolve().parents[2]
FORMATS = ('trx', 'pytest', 'vitest', 'go', 'rust')


def is_tag(element, name):
    return element.tag.rsplit('}', 1)[-1] == name


def junit_durations(report):
    rows = []
    for case in ET.parse(report).iter():
        if not is_tag(case, 'testcase') or 'time' not in case.attrib:
            continue
        name = case.attrib.get('name', 'test')
        owner = case.attrib.get('classname')
        rows.append((float(case.attrib['time']), f'{owner}.{name}' if owner else name))
    return rows


def go_durations(capture):
    rows = []
    for line in capture.splitlines():
        try:
            event = json.loads(line)
        except ValueError:
            continue
        if event.get('Action') not in ('pass', 'fail') or not event.get('Test'):
            continue
        rows.append((float(event.get('Elapsed', 0)), f"{event['Package']}.{event['Test']}"))
    return rows


RUNNING = re.compile(r'^\s+Running (.+) \([^)]+\)$')


def rust_durations(markings):
    rows = []
    started = None
    label = None
    for stamp, line in markings:
        match = RUNNING.match(line)
        if not match:
            continue
        if started is not None:
            rows.append((stamp - started, label))
        started, label = stamp, match.group(1)
    return rows


def durations(report):
    rows = []
    for result in ET.parse(report).iter():
        if not is_tag(result, 'UnitTestResult'):
            continue
        hours, minutes, seconds = result.attrib['duration'].split(':')
        elapsed = int(hours) * 3600 + int(minutes) * 60 + float(seconds)
        rows.append((elapsed, result.attrib['testName']))
    if not rows:
        raise ValueError('Fast runner produced no test timings')
    return sorted(rows, reverse=True)


def enforce(rows, elapsed, suite, note=None):
    if note:
        print(note, flush=True)
    unit = 'test binaries' if suite == 'sdk-rust' else 'tests'
    print(f'Fast suite: {elapsed:.3f}s / {BUDGET_SECONDS}s; {len(rows)} {unit}. Slowest 10:', flush=True)
    for seconds, name in rows[:10]:
        print(f'  {seconds:8.3f}s {name}', flush=True)
    for seconds, name in rows:
        if seconds > TEST_SECONDS:
            print(f'FAILED Fast test exceeded {TEST_SECONDS}s: {name} ({seconds:.3f}s)',
                  file=sys.stderr, flush=True)
    if elapsed >= BUDGET_SECONDS:
        print(f'FAILED Fast suite exceeded {BUDGET_SECONDS}s: {suite} ({elapsed:.3f}s)',
              file=sys.stderr, flush=True)
    return elapsed >= BUDGET_SECONDS or any(seconds > TEST_SECONDS for seconds, _ in rows)


def check(report, elapsed):
    return enforce(durations(report), elapsed, Path(report).stem)


def main():
    args = sys.argv[1:]
    fmt = 'trx'
    if '--format' in args:
        position = args.index('--format')
        if position + 2 >= len(args) or args[position + 2] != '--' or args[position + 1] not in FORMATS:
            sys.exit(f'usage: fast-budget.py <suite> [--format {"|".join(FORMATS)}] -- <command>')
        fmt = args[position + 1]
        args = args[:position] + args[position + 2:]
    if len(args) < 2 or args[1] != '--':
        sys.exit(f'usage: fast-budget.py <suite> [--format {"|".join(FORMATS)}] -- <command>')
    suite, command = args[0], args[2:]
    if not command:
        sys.exit('usage: fast-budget.py <suite> [--format ...] -- <command>')
    results = ROOT / 'artifacts/test-results'
    results.mkdir(parents=True, exist_ok=True)
    report = results / f'{suite}-{uuid.uuid4().hex}.xml'
    if fmt == 'trx':
        report = report.with_suffix('.trx')
        command += ['--report-trx', '--results-directory', str(results),
                    '--report-trx-filename', report.name, '--no-ansi']
    elif fmt == 'pytest':
        command += ['--junitxml', str(report)]
    elif fmt == 'vitest':
        command += ['--', '--reporter=junit', f'--outputFile={report}']
    elif fmt == 'go':
        command += ['-json']
    report.unlink(missing_ok=True)
    started = time.monotonic()
    streamed = fmt in ('go', 'rust')
    process = subprocess.Popen(command, start_new_session=True,
                               stdout=subprocess.PIPE if streamed else None,
                               stderr=subprocess.STDOUT if streamed else None,
                               text=True)
    def cancel(signum, frame):
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()
        sys.exit(128 + signum)
    for signum in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        signal.signal(signum, cancel)
    output = []
    def pump():
        if streamed and process.stdout:
            for line in process.stdout:
                if fmt == 'rust':
                    sys.stdout.write(line)
                    sys.stdout.flush()
                output.append((time.monotonic(), line))
    reader = threading.Thread(target=pump)
    reader.start()
    try:
        code = process.wait(timeout=KILL_SECONDS)
        reader.join(timeout=5)
    except subprocess.TimeoutExpired:
        print(f'FAILED Fast suite exceeded {BUDGET_SECONDS}s and was killed at {KILL_SECONDS}s: {suite}',
              file=sys.stderr, flush=True)
        code = 124
    finally:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()
        reader.join(timeout=5)
    elapsed = time.monotonic() - started
    text = ''.join(line for _, line in output)
    if fmt == 'go' and code != 0:
        sys.stdout.write(text)
        sys.stdout.flush()
    note = ('sdk-rust: stable libtest has no per-test timing; enforcing wall-clock per test binary'
            if fmt == 'rust' else None)
    rows = []
    if fmt == 'trx' and report.is_file():
        rows = durations(report)
    elif fmt in ('pytest', 'vitest') and report.is_file():
        rows = junit_durations(report)
    elif fmt == 'go':
        rows = go_durations(text)
    elif fmt == 'rust':
        rows = rust_durations(output)
    if rows:
        code = int(enforce(rows, elapsed, suite, note)) or code
    elif code == 0:
        sys.exit(f'FAILED Fast suite produced no {"TRX report" if fmt == "trx" else "timings"}')
    return code


if __name__ == '__main__':
    sys.exit(main())
