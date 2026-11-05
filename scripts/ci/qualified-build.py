#!/usr/bin/env python3
"""Publish source-bound checked-build receipts; runtime payloads stay in CI artifacts."""
import hashlib
from decimal import Decimal
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import urllib.request

REPO = 'TerraScale/terrascale'


def gh(*args):
    return subprocess.check_output(['gh', *args], text=True)


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def statuses(sha):
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        # Commit statuses are public. Preserve the existing anonymous lookup so
        # verification jobs need no additional token permission.
        pages = []
        url = f'https://api.github.com/repos/{REPO}/commits/{sha}/statuses?per_page=100'
        while url:
            request = urllib.request.Request(url, headers={'Accept': 'application/vnd.github+json'})
            with urllib.request.urlopen(request, timeout=20) as response:
                pages.append(json.load(response))
                link = response.headers.get('Link', '')
                next_page = re.search(r'<([^>]+)>; rel="next"', link)
                url = next_page[1] if next_page else None
    else:
        pages = json.loads(gh('api', '--paginate', '--slurp', f'repos/{REPO}/commits/{sha}/statuses?per_page=100'))
    latest = {}
    for page in pages:
        for row in page:
            if row['id'] > latest.get(row['context'], {}).get('id', 0):
                latest[row['context']] = row
    return latest


def trusted(row):
    return row and row['state'] == 'success' and row['creator']['login'].lower() == 'mariogk'


def documentation(path):
    """Match shipping's docs-only exception; application Markdown remains input."""
    name = Path(path)
    if path.startswith('.cos/') or name.name in {'AGENTS.md', 'CLAUDE.md'}:
        return True
    if path.startswith('docs-site/'):
        return False
    return ((path.startswith('docs/') and name.suffix.lower() in {'.png', '.jpg', '.jpeg', '.gif', '.webp', '.svg'})
            or (name.suffix.lower() in {'.md', '.mdx', '.rst'}
                and not path.startswith(('src/', 'tests/', 'scripts/', 'sdk/'))))


def git(*args):
    return subprocess.check_output(['git', *args], text=True).strip()


def proof_source(area, sha):
    """Find proof across docs-only parents; never replace a newer area result."""
    current = sha
    rows = statuses(current)
    contexts = {'ci-build/' + area, 'ts-ship/' + area}
    if contexts & rows.keys():
        return current, rows
    if git('rev-parse', 'HEAD') != sha:
        raise ValueError('Inherited proof requires checkout of the requested revision')
    # Actions checks out one commit. Fetch only a bounded ancestry and its trees;
    # name-only diffs do not need historical blobs. No new token is required.
    if git('rev-parse', '--is-shallow-repository') == 'true':
        git('fetch', '--no-tags', '--filter=blob:none', '--depth=65', 'origin', sha)
    for _ in range(64):
        parents = git('rev-list', '--parents', '-n', '1', current).split()[1:]
        if len(parents) != 1:
            break  # Roots, shallow boundaries and merges need their own proof.
        parent = parents[0]
        paths = git('diff', '--name-only', '--no-renames', '-z', parent, current).split('\0')
        if any(not documentation(path) for path in paths if path):
            break
        current = parent
        rows = statuses(current)
        if contexts & rows.keys():
            return current, rows
    raise ValueError(f'No checked-build proof across documentation-only parents for {area} at {sha}; run ts-ship on a dev host')


def verify(area, sha, directory, require_fast=True, rows=None):
    if rows is None:
        rows = statuses(sha)
    build = rows.get('ci-build/' + area)
    if not trusted(build):
        raise ValueError(f'No trusted exact-commit checked build for {area} at {sha}; run ts-ship on a dev host')
    match = re.fullmatch(r'Checked build; manifest sha256 ([0-9a-f]{64})', build['description'])
    if not match:
        raise ValueError('Invalid checked-build digest')
    if require_fast:
        fast = rows.get('ts-ship/' + area)
        timing = re.match(r'Fast passed in ((?:0|[1-9][0-9]*)(?:\.[0-9]+)?)s \(<30s; tests <=10s\); test times reported;', (fast or {}).get('description', ''))
        if not trusted(fast) or not timing or Decimal(timing[1]) >= Decimal(30):
            raise ValueError(f'No trusted Fast proof with enforced 30s/10s budgets for {area}')
        print(f'{area}: {fast["description"]}', flush=True)
    gh('release', 'download', f'ci/qualified-{sha}-{area}', '--repo', REPO,
       '--pattern', 'manifest.json', '--dir', str(directory))
    path = directory / 'manifest.json'
    if digest(path) != match[1]:
        raise ValueError('Checked-build manifest digest mismatch')
    manifest = json.loads(path.read_text())
    if manifest['schema'] != 'terrascale.ci-checks/v2' or manifest['revision'] != sha or manifest['area'] != area:
        raise ValueError('Checked build belongs to another revision or area')
    if set(manifest['files']) != {'checks.log'}:
        raise ValueError('Checked receipts may contain logs only')
    for name, checksum in manifest['files'].items():
        if not re.fullmatch(r'[0-9a-f]{64}', checksum):
            raise ValueError('Invalid checked-log checksum')
    return manifest


def prepare(area, sha, directory):
    with tempfile.TemporaryDirectory(prefix='ci-receipt-') as scratch:
        try:
            verify(area, sha, Path(scratch), require_fast=False)
            print('Reusing existing exact-commit checked build', flush=True)
            return
        except (ValueError, subprocess.CalledProcessError):
            pass
    env = dict(os.environ, CI='true', RUNNER_TEMP=str(directory), TS_AUTH_REVISION=sha,
               TERRABASE_REVISION=sha, SourceRevisionId=sha, ContinuousIntegrationBuild='true',
               DeterministicSourcePaths='false')
    # Runtime-specific executables consume portable project-reference outputs.
    # Auth pins the SDK reference's framework, which bypasses MSBuild's normal
    # RID-agnostic negotiation; retain its existing portable locked assets.
    env['_GlobalPropertiesToRemoveFromProjectReferences'] = ';RuntimeIdentifier;SelfContained'
    # Leave Fast's planning phase; the CLI wrapper still bounds compilation.
    env.pop('TS_SHIP_PHASE', None)
    env.pop('TS_SHIP_MANIFEST', None)
    log = directory / 'checks.log'
    command = {
        'libs': 'bash scripts/ci/test-area.sh build libs',
        'auth': 'bash scripts/ci/auth-build.sh',
        'terrabase': 'bash scripts/ci/test-area.sh build terrabase && make -j2 -o restore -o terrabase-build terrabase-e2e-local CONFIGURATION=Release',
    }[area]
    with log.open('w') as stream:
        with subprocess.Popen(['bash', '-euo', 'pipefail', '-c', 'make restore && ' + command], env=env,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True) as result:
            for line in result.stdout:
                stream.write(line)
                print(line, end='', flush=True)
    if result.returncode:
        raise ValueError(f'{area} checked build failed (exit {result.returncode})')
    files = [log]
    manifest = {'schema': 'terrascale.ci-checks/v2', 'revision': sha, 'area': area,
                'files': {path.name: digest(path) for path in files}}
    path = directory / 'manifest.json'
    path.write_text(json.dumps(manifest, sort_keys=True) + '\n')
    tag = f'ci/qualified-{sha}-{area}'
    # Existing exact-commit receipts are immutable. Reuse them after rerunning Fast.
    with tempfile.TemporaryDirectory(prefix='ci-receipt-') as scratch:
        try:
            verify(area, sha, Path(scratch), require_fast=False)
            print('Reusing existing exact-commit checked build', flush=True)
            return
        except (ValueError, subprocess.CalledProcessError):
            pass
    gh('release', 'create', tag, '--repo', REPO, '--target', sha, '--prerelease',
       '--title', f'CI checked {area} {sha}', '--notes', f'Dev-host checked build for {sha}.',
       str(path), *(str(file) for file in files))
    gh('api', '--method', 'POST', f'repos/{REPO}/statuses/{sha}', '-f', 'state=success',
       '-f', 'context=ci-build/' + area, '-f', 'description=Checked build; manifest sha256 ' + digest(path),
       '-f', f'target_url=https://github.com/{REPO}/releases/tag/{tag}', '--silent')


def main():
    action, area = sys.argv[1:3]
    if area not in {'libs', 'auth', 'terrabase'}:
        raise ValueError('Expected libs, auth or terrabase')
    sha = os.environ.get('GITHUB_SHA') or subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('Expected exact commit SHA')
    if action == 'prepare':
        if subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip() != sha or subprocess.check_output(['git', 'status', '--porcelain'], text=True).strip():
            raise ValueError('Checked build requires a clean checkout of its exact revision')
        with tempfile.TemporaryDirectory(prefix='ci-build-') as scratch:
            prepare(area, sha, Path(scratch))
    elif action == 'verify':
        with tempfile.TemporaryDirectory(prefix='ci-proof-') as scratch:
            source, rows = proof_source(area, sha)
            verify(area, source, Path(scratch), rows=rows)
            if source != sha:
                print(f'{area}: reused checked build and Fast proof from {source} for {sha}; documentation-only ancestry', flush=True)
    else:
        raise ValueError('Expected prepare or verify')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        sys.exit('FAILED ' + str(error))
