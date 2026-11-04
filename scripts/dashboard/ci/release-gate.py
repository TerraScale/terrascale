#!/usr/bin/env python3
"""Select the dashboard revision to release: the newest qualified main commit.

The release token's sha claim must equal the image's revision label, so a
revision can only be released by a push run of that revision, or a dispatch
started while it is the real main head. Later unrelated pushes do not block
push or dispatch releases:

  release <sha>  exit 0 and print true when <sha> is the newest qualified,
                 not-failed main commit, its CI passed and it is newer than live.
  heal           after CI completes, rerun its Images run, or dispatch Images
                 on main if GitHub missed the push and it is still the head.
"""
import functools
import json
import os
import subprocess
import sys
import time
import urllib.request

REPO = 'TerraScale/terrascale'
CONTEXT = 'dashboard-release/dashboard'
WORKFLOW = 'images-ghcr.yml'
RERUNS = 3


def gh(*args):
    return json.loads(subprocess.check_output(['gh', *args], text=True))


# Proofs are posted before main moves; one lookup per commit per run suffices.
@functools.cache
def qualified(sha):
    pages = gh('api', '--paginate', '--slurp', f'repos/{REPO}/commits/{sha}/statuses?per_page=100')
    proof = next((item for page in pages for item in page if item['context'] == CONTEXT), None)
    return bool(proof) and proof['state'] == 'success' and proof['creator']['login'] == 'MarioGK'


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
            test = jobs.get('test')
            # Native maintenance deliberately has no qualifying test job. It cannot turn CI green.
            if run['event'] == 'workflow_dispatch' and test is None:
                continue
            if run['conclusion'] != 'success' or test != 'success':
                return 'failed'
            # Normal recovery selects both core services. Their jobs can succeed only
            # for refs/heads/main, so a branch/tag dispatch cannot qualify as main CI.
            if run['event'] == 'workflow_dispatch' and any(
                    jobs.get(name) != 'success' for name in ('main', 'main-auth')):
                return 'failed'
        qualifying.append(run)
    if qualifying and all(item['status'] == 'completed' for item in qualifying):
        return 'green'
    return 'pending'


def main_head():
    return gh('api', f'repos/{REPO}/git/ref/heads/main')['object']['sha']


def live():
    try:
        with urllib.request.urlopen('https://dash.terrascale.tech/readyz', timeout=30) as response:
            return response.headers.get('X-TerraScale-Revision')
    except OSError:
        return None


def target():
    """Newest qualified main commit newer than live whose CI has not failed."""
    deployed = live()
    for page in range(1, 4):
        commits = gh('api', f'repos/{REPO}/commits?sha=main&per_page=50&page={page}')
        for commit in commits:
            sha = commit['sha']
            if sha == deployed:
                return None, deployed
            if qualified(sha):
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
    deadline = time.monotonic() + 25 * 60
    while True:
        # GitHub binds a main dispatch to the head at creation. Like a push run,
        # it keeps signing that SHA after unrelated main pushes; target() still
        # rejects it if a newer qualified revision exists.
        if (os.environ.get('GITHUB_EVENT_NAME') == 'workflow_dispatch'
                and os.environ.get('GITHUB_REF') != 'refs/heads/main'):
            print(f'Recovery dispatch {revision} is not a main-ref run', file=sys.stderr)
            return False
        found, deployed = target()
        if found is None or found[0] != revision:
            print(f'{revision} is not the newest qualified unreleased main commit ({found}, live {deployed})',
                  file=sys.stderr)
            return False
        if found[1] == 'green':
            return newer_than(deployed, revision)
        if time.monotonic() > deadline:
            print(f'CI for {revision} did not finish; the CI completion heal reruns this run', file=sys.stderr)
            return False
        time.sleep(30)


def heal():
    found, deployed = target()
    if found is None or found[1] != 'green' or not newer_than(deployed, found[0]):
        print(f'Nothing to heal ({found}, live {deployed})')
        return
    sha = found[0]
    runs = gh('run', 'list', '--repo', REPO, '--workflow', WORKFLOW, '--branch', 'main', '--commit', sha,
              '--limit', '100', '--json', 'databaseId,event,status,attempt')
    runs = [run for run in runs if run['event'] in ('push', 'workflow_dispatch')]
    if not runs:
        if main_head() != sha:
            print(f'{sha} has no Images run and main advanced; qualify the current head on a dev host',
                  file=sys.stderr)
            return
        subprocess.check_call(['gh', 'workflow', 'run', WORKFLOW, '--repo', REPO, '--ref', 'main'])
        print(f'Dispatched {WORKFLOW} on main to release {sha} (live {deployed})')
        return
    run = max(runs, key=lambda item: item['databaseId'])
    if run['status'] != 'completed':
        print(f'Run {run["databaseId"]} for {sha} is still {run["status"]}')
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
