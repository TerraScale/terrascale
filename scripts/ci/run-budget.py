#!/usr/bin/env python3
"""Enforce elapsed wall time, including setup and all production work."""
from datetime import datetime, timezone
import json
import subprocess
import sys


def api(path):
    return json.loads(subprocess.check_output(['gh', 'api', path], text=True))


def instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


run = api('repos/TerraScale/terrascale/actions/runs/' + sys.argv[1])
jobs = api(run['jobs_url'] + '?per_page=100')['jobs']
elapsed = (datetime.now(timezone.utc) - instant(run['run_started_at'])).total_seconds()
errors = []
for job in jobs:
    if job['name'] in {'libs', 'auth', 'terrabase'} and job['conclusion'] != 'skipped':
        duration = (instant(job['completed_at']) - instant(job['started_at'])).total_seconds()
        print(f'{job["name"]}: {duration:.0f}s / 120s')
        if duration > 120 or job['conclusion'] != 'success':
            errors.append(f'{job["name"]} did not pass within 120s')
    if job['status'] == 'completed' and job['conclusion'] not in {'success', 'skipped'}:
        errors.append(f'{job["name"]}: {job["conclusion"]}')
print(f'CI: {elapsed:.0f}s / 360s')
if elapsed > 360:
    errors.append('CI exceeded 360s')
if errors:
    sys.exit('FAILED ' + '; '.join(errors))
