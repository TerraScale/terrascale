#!/usr/bin/env python3
"""Select the dashboard revision to release: the newest qualified main commit.

The release token's sha claim must equal the image's revision label, so a
revision can only be released by its own push run (or a rerun of it). Later
pushes outside the dashboard therefore never block it:

  release <sha>  exit 0 and print true when <sha> is the newest qualified,
                 not-failed main commit, its CI passed and it is newer than live.
  heal           after CI completes, rerun the push run of that commit when it
                 ended without releasing it.
"""
import functools
import json
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
              '--event', 'push', '--limit', '100', '--json', 'status,conclusion')
    if any(item['status'] == 'completed' and item['conclusion'] != 'success' for item in runs):
        return 'failed'
    if runs and all(item['status'] == 'completed' for item in runs):
        return 'green'
    return 'pending'


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
    runs = gh('run', 'list', '--repo', REPO, '--workflow', WORKFLOW, '--commit', sha, '--event', 'push',
              '--limit', '10', '--json', 'databaseId,status,attempt')
    if not runs:
        print(f'{sha} has no push run of {WORKFLOW}; dispatch cannot sign it', file=sys.stderr)
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
