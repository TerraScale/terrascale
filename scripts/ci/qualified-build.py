#!/usr/bin/env python3
"""Retain and restore source-bound dev-host CI builds. No compiled code runs on Actions."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import shutil
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


def verify(area, sha, directory, require_fast=True):
    rows = statuses(sha)
    build = rows.get('ci-build/' + area)
    if not trusted(build):
        raise ValueError(f'No trusted exact-commit checked build for {area} at {sha}; run ts-ship on a dev host')
    match = re.fullmatch(r'Checked build; manifest sha256 ([0-9a-f]{64})', build['description'])
    if not match:
        raise ValueError('Invalid checked-build digest')
    if require_fast:
        fast = rows.get('ts-ship/' + area)
        timing = re.match(r'Fast passed in ([0-9.]+)s \(<120s\); tests <=10s;', (fast or {}).get('description', ''))
        if not trusted(fast) or not timing or float(timing[1]) >= 120:
            raise ValueError(f'No trusted Fast proof within 120s/10s budgets for {area}')
        print(f'{area}: {fast["description"]}', flush=True)
    gh('release', 'download', f'ci/qualified-{sha}-{area}', '--repo', REPO,
       '--pattern', 'manifest.json', '--dir', str(directory))
    path = directory / 'manifest.json'
    if digest(path) != match[1]:
        raise ValueError('Checked-build manifest digest mismatch')
    manifest = json.loads(path.read_text())
    if manifest['schema'] != 'terrascale.ci-build/v1' or manifest['revision'] != sha or manifest['area'] != area:
        raise ValueError('Checked build belongs to another revision or area')
    if area != 'libs':
        registry = 'ghcr.io/terrascale/' + ('ts-auth' if area == 'auth' else 'terrabase')
        if not re.fullmatch(re.escape(registry) + r'@sha256:[0-9a-f]{64}', manifest.get('image', '')):
            raise ValueError('Invalid checked runtime image')
    return manifest


def restore(area, sha, directory):
    manifest = verify(area, sha, directory)
    for name, checksum in manifest['files'].items():
        if Path(name).name != name or not re.fullmatch(r'[0-9a-f]{64}', checksum):
            raise ValueError('Invalid artifact entry')
        gh('release', 'download', f'ci/qualified-{sha}-{area}', '--repo', REPO,
           '--pattern', name, '--dir', str(directory))
        if digest(directory / name) != checksum:
            raise ValueError('Artifact checksum mismatch: ' + name)
    print(f'Restored checked {area} build for {sha}', flush=True)


def publish_image(area, sha, directory, env):
    engine = shutil.which('podman')
    if not engine:
        raise ValueError('Podman is required for dev-host runtime packaging')
    registry = 'ghcr.io/terrascale/' + ('ts-auth' if area == 'auth' else 'terrabase')
    authfile = directory / 'registry-auth.json'
    # Existing gh credentials are delivered over stdin, never through arguments or logs.
    token = subprocess.Popen(['gh', 'auth', 'token'], stdout=subprocess.PIPE)
    try:
        subprocess.run([engine, 'login', '--authfile', str(authfile), '--username', 'MarioGK',
                        '--password-stdin', 'ghcr.io'], stdin=token.stdout, check=True)
    finally:
        token.stdout.close()
        if token.wait():
            raise ValueError('Could not read existing gh credential')
    runtime = directory / 'image-payloads'
    dockerfile = runtime / 'Dockerfile'
    with dockerfile.open('w') as output:
        subprocess.run(['bash', 'scripts/ci/image-payloads.sh', 'runtime-dockerfile', area],
                       stdout=output, check=True, env=env)
    index = 'localhost/ci-' + area + ':' + sha + '-' + directory.name
    images = []
    try:
        subprocess.run([engine, 'manifest', 'create', index], check=True)
        for arch in ('amd64', 'arm64'):
            name = index + '-' + arch
            subprocess.run([engine, 'build', '--platform', 'linux/' + arch,
                            '--build-arg', 'TARGETARCH=' + arch,
                            '--label', 'org.opencontainers.image.revision=' + sha,
                            '--label', 'org.opencontainers.image.source=https://github.com/' + REPO,
                            '--tag', name, str(runtime)], check=True)
            images.append(name)
            subprocess.run([engine, 'manifest', 'add', index, 'containers-storage:' + name], check=True)
        digestfile = directory / 'image-digest'
        subprocess.run([engine, 'manifest', 'push', '--all', '--authfile', str(authfile),
                        '--digestfile', str(digestfile), index,
                        'docker://' + registry + ':qualified-' + sha], check=True)
        value = digestfile.read_text().strip()
        if not re.fullmatch(r'sha256:[0-9a-f]{64}', value):
            raise ValueError('Invalid published OCI digest')
        return registry + '@' + value
    finally:
        subprocess.run([engine, 'manifest', 'rm', index], check=False)
        for name in images:
            subprocess.run([engine, 'image', 'rm', name], check=False)
        authfile.unlink(missing_ok=True)


def prepare(area, sha, directory):
    with tempfile.TemporaryDirectory(prefix='ci-receipt-') as scratch:
        try:
            verify(area, sha, Path(scratch), require_fast=False)
            print('Reusing existing exact-commit checked build', flush=True)
            return
        except (ValueError, subprocess.CalledProcessError):
            pass
    env = dict(os.environ, RUNNER_TEMP=str(directory), TS_AUTH_REVISION=sha,
               TERRABASE_REVISION=sha, SourceRevisionId=sha, ContinuousIntegrationBuild='true',
               DeterministicSourcePaths='false')
    # Use the real CLI after phased tests; all checks and packaging run once.
    env.pop('TS_SHIP_PHASE', None)
    env.pop('TS_SHIP_MANIFEST', None)
    log = directory / 'checks.log'
    command = {
        'libs': 'bash scripts/ci/test-area.sh build libs',
        'auth': 'bash scripts/ci/auth-build.sh && make auth-publish RUNTIME=linux-x64 PUBLISH_DIR="$RUNNER_TEMP/auth-publish" && bash scripts/ci/image-payloads.sh auth',
        'terrabase': 'bash scripts/ci/test-area.sh build terrabase && make -j2 -o restore -o terrabase-build terrabase-e2e-local CONFIGURATION=Release && bash scripts/ci/image-payloads.sh terrabase amd64 && bash scripts/ci/image-payloads.sh terrabase arm64',
    }[area]
    with log.open('w') as stream:
        result = subprocess.run(['bash', '-euo', 'pipefail', '-c', command], env=env,
                                stdout=stream, stderr=subprocess.STDOUT)
    print(log.read_text(), end='', flush=True)
    if result.returncode:
        raise ValueError(f'{area} checked build failed (exit {result.returncode})')
    image = None
    if area != 'libs':
        image = publish_image(area, sha, directory, env)
    files = [log]
    manifest = {'schema': 'terrascale.ci-build/v1', 'revision': sha, 'area': area,
                'files': {path.name: digest(path) for path in files}, 'image': image}
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
    elif action == 'restore':
        restore(area, sha, Path(os.environ['RUNNER_TEMP']))
    elif action == 'verify':
        with tempfile.TemporaryDirectory(prefix='ci-proof-') as scratch:
            verify(area, sha, Path(scratch))
    else:
        raise ValueError('Expected prepare, restore or verify')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        sys.exit('FAILED ' + str(error))
