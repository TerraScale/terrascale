#!/usr/bin/env python3
"""Report Fast job times and end-to-end CI time against the 360 s target."""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys


def instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def check(run, jobs, now):
    elapsed = (now - instant(run['run_started_at'])).total_seconds()
    errors, lines = [], []
    for job in jobs:
        if job['conclusion'] == 'skipped':
            continue
        if job['name'] in {'libs', 'auth', 'terrabase'}:
            if job['status'] != 'completed':
                errors.append(f'{job["name"]} has not completed')
                continue
            duration = (instant(job['completed_at']) - instant(job['started_at'])).total_seconds()
            # Compilation dominates a hosted job; fast-budget.py enforces the 30 s suite and 10 s test limits.
            lines.append(f'{job["name"]}: {duration:.0f}s')
            if job['conclusion'] != 'success':
                errors.append(f'{job["name"]} did not pass')
        if job['status'] == 'completed' and job['conclusion'] not in {'success', 'skipped'}:
            errors.append(f'{job["name"]}: {job["conclusion"]}')
    lines.append(f'CI including rollout: {elapsed:.0f}s / 360s')
    # ts-orch's rollout time is outside CI's control: warn rather than fail.
    if elapsed > 360:
        lines.append('::warning::CI including rollout exceeded 360s')
    return errors, lines


def main():
    def api(path):
        return json.loads(subprocess.check_output(['gh', 'api', path], text=True))
    run = api('repos/TerraScale/terrascale/actions/runs/' + sys.argv[1])
    pages = json.loads(subprocess.check_output(['gh', 'api', '--paginate', '--slurp',
                       run['jobs_url'] + '?per_page=100'], text=True))
    errors, lines = check(run, [job for page in pages for job in page['jobs']], datetime.now(timezone.utc))
    print('\n'.join(lines))
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with Path(os.environ['GITHUB_STEP_SUMMARY']).open('a') as output:
            output.write('\n'.join(line.replace('::warning::', 'Warning: ') for line in lines) + '\n')
    if errors:
        sys.exit('FAILED ' + '; '.join(errors))


if __name__ == '__main__':
    main()
