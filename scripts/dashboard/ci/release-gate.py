#!/usr/bin/env python3
import functools
import json
import os
import re
import subprocess
import sys
import urllib.request

REPO = 'TerraScale/terrascale'
WORKFLOW = 'images-ghcr.yml'
RERUNS = 3


def gh(*args):
    return json.loads(subprocess.check_output(['gh', *args], text=True))


@functools.cache
def qualified(sha):
    runs = images_runs(sha)
    return bool(runs) and not all(run['status'] == 'completed' and run['conclusion'] != 'success'
                                  and test_failed(run) for run in runs)


def test_failed(run):
    pages = gh('api', '--paginate', '--slurp',
               f'repos/{REPO}/actions/runs/{run["databaseId"]}/jobs?filter=latest&per_page=100')
    return any(job['name'].startswith('test') and job['conclusion'] not in ('success', 'skipped', None)
               for page in pages for job in page['jobs'])


def ci(sha):
    runs = gh('run', 'list', '--repo', REPO, '--workflow', 'ci.yml', '--branch', 'main', '--commit', sha,
              '--limit', '100', '--json', 'databaseId,event,status,conclusion')
    qualifying = []
    for run in runs:
        if run['event'] not in ('push', 'workflow_dispatch'):
            continue
        if run['status'] == 'completed':
            pages = gh('api', '--paginate', '--slurp',
                       f'repos/{REPO}/actions/runs/{run["databaseId"]}/jobs?filter=latest&per_page=100')
            jobs = {job['name']: job['conclusion'] for page in pages for job in page['jobs']}
            test = next((conclusion for name, conclusion in jobs.items()
                         if name == 'test' or name.endswith("|| 'test'")), None)
            if run['event'] == 'workflow_dispatch' and test is None:
                continue
            passed = {'success', 'skipped'} if run['event'] == 'push' else {'success'}
            if run['conclusion'] != 'success' or test not in passed:
                return 'failed'
            if run['event'] == 'workflow_dispatch' and any(
                    jobs.get(name) != 'success' for name in ('main', 'main-auth-ordered')):
                return 'failed'
        qualifying.append(run)
    if qualifying and all(item['status'] == 'completed' for item in qualifying):
        return 'green'
    return 'pending'


def main_head():
    return gh('api', f'repos/{REPO}/git/ref/heads/main')['object']['sha']


def images_runs(sha):
    runs = gh('run', 'list', '--repo', REPO, '--workflow', WORKFLOW, '--branch', 'main', '--commit', sha,
              '--limit', '100', '--json', 'databaseId,event,status,conclusion,attempt')
    return [run for run in runs if run['event'] in ('push', 'workflow_dispatch')]


def live():
    with urllib.request.urlopen('https://dash.terrascale.tech/readyz', timeout=30) as response:
        revision = response.headers.get('X-TerraScale-Revision', '')
    if not re.fullmatch('[0-9a-f]{40}', revision):
        raise SystemExit('Dashboard readiness has no valid source revision')
    return revision


def target():
    deployed = live()
    head = main_head()
    for page in range(1, 4):
        commits = gh('api', f'repos/{REPO}/commits?sha=main&per_page=50&page={page}')
        for commit in commits:
            sha = commit['sha']
            if sha == deployed:
                return None, deployed
            if qualified(sha):
                if sha != head and not images_runs(sha):
                    print(f'Skipping {sha}: main advanced and no signed Images run exists', file=sys.stderr)
                    continue
                state = ci(sha)
                if state != 'failed':
                    return (sha, state), deployed
        if len(commits) < 50:
            break
    return None, deployed


def newer_than(base, head):
    if not base:
        return True
    return gh('api', f'repos/{REPO}/compare/{base}...{head}')['status'] == 'ahead'


def release(revision):
    if (os.environ.get('GITHUB_EVENT_NAME') == 'workflow_dispatch'
            and os.environ.get('GITHUB_REF') != 'refs/heads/main'):
        print(f'Recovery dispatch {revision} is not a main-ref run', file=sys.stderr)
        return False
    found, deployed = target()
    if found is None or found[0] != revision:
        print(f'{revision} is not the newest qualified unreleased main commit ({found}, live {deployed})',
              file=sys.stderr)
        return False
    return newer_than(deployed, revision)


def heal():
    found, deployed = target()
    if found is None or found[1] != 'green' or not newer_than(deployed, found[0]):
        print(f'Nothing to heal ({found}, live {deployed})')
        return
    sha = found[0]
    runs = images_runs(sha)
    if not runs:
        if main_head() != sha:
            print(f'{sha} has no Images run and main advanced; a newer push releases the dashboard',
                  file=sys.stderr)
            return
        subprocess.check_call(['gh', 'workflow', 'run', WORKFLOW, '--repo', REPO, '--ref', 'main'])
        print(f'Dispatched {WORKFLOW} on main to release {sha} (live {deployed})')
        return
    dispatched = [run for run in runs if run['event'] == 'workflow_dispatch' and run['status'] != 'completed']
    if dispatched:
        print(f'Dispatch {dispatched[0]["databaseId"]} already owns the qualified deployment handoff')
        return
    run = max(runs, key=lambda item: item['databaseId'])
    if run['status'] != 'completed':
        subprocess.check_call(['gh', 'workflow', 'run', WORKFLOW, '--repo', REPO, '--ref', 'main'])
        print(f'Dispatched {WORKFLOW} on main after CI qualified {sha} (Images still {run["status"]})')
        return
    if run['attempt'] >= RERUNS:
        raise SystemExit(f'Run {run["databaseId"]} for {sha} already ran {run["attempt"]} attempts')
    subprocess.check_call(['gh', 'run', 'rerun', str(run['databaseId']), '--repo', REPO])
    print(f'Reran {run["databaseId"]} to release {sha} (live {deployed})')


if __name__ == '__main__':
    if sys.argv[1:2] == ['heal'] and len(sys.argv) == 2:
        heal()
    elif sys.argv[1:2] == ['release'] and len(sys.argv) == 3:
        print('true' if release(sys.argv[2]) else 'false')
    else:
        raise SystemExit('usage: release-gate.py release <sha> | heal')
