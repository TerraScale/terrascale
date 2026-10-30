#!/usr/bin/env python3
"""Require blocking main checks while preserving the repository's nonblocking Slow policy."""
import json
import subprocess
import sys

SLOW_JOBS = {'slow-auth-main', 'slow-terrabase-main', 'slow-auth', 'slow-terrabase'}


def validate(runs, jobs_by_run):
    if not runs:
        raise ValueError('Main push CI is missing')
    ci = False
    slow_failures = []
    for run in runs:
        if run['name'] != 'CI':
            if run['status'] != 'completed' or run['conclusion'] not in ('success', 'skipped'):
                raise ValueError(f"Blocking workflow incomplete or failed: {run['name']}")
            continue
        ci = True
        jobs = jobs_by_run[run['databaseId']]
        rollup = [job for job in jobs if job['name'] == 'test']
        if len(rollup) != 1 or rollup[0]['conclusion'] != 'success':
            raise ValueError('The required main test rollup must succeed')
        failed = [job['name'] for job in jobs if job['conclusion'] == 'failure']
        if any(name not in SLOW_JOBS for name in failed):
            raise ValueError('A blocking CI job failed')
        slow_failures.extend(failed)
    if not ci:
        raise ValueError('Main CI test rollup is missing')
    return slow_failures


def gh(*args):
    return json.loads(subprocess.check_output(['gh', *args], text=True))


def main():
    revision = sys.argv[1]
    runs = gh('run', 'list', '--repo', 'TerraScale/terrascale', '--branch', 'main',
              '--commit', revision, '--event', 'push', '--limit', '100',
              '--json', 'databaseId,name,status,conclusion')
    jobs = {run['databaseId']: gh('run', 'view', str(run['databaseId']), '--repo',
                                 'TerraScale/terrascale', '--json', 'jobs')['jobs']
            for run in runs if run['name'] == 'CI'}
    slow_failures = validate(runs, jobs)
    print(f'Verified blocking main CI and test rollup for {revision}; '
          f'nonblocking Slow failures: {", ".join(slow_failures) or "none"}')


if __name__ == '__main__':
    main()
