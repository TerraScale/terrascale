#!/usr/bin/env python3
"""Deploy qualified monorepo main using the operator host's existing cf authentication."""
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

AREA = Path(__file__).resolve().parents[1]
ROOT = AREA.parent


def run(*command, capture=False, cwd=ROOT):
    return subprocess.run(command, cwd=cwd, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None).stdout


def main():
    if run('git', 'status', '--porcelain', capture=True).strip():
        raise SystemExit('Commit all source changes before deploying')
    revision = run('git', 'rev-parse', 'HEAD', capture=True).strip()
    if not re.fullmatch(r'[0-9a-f]{40}', revision):
        raise SystemExit('Expected a full source revision')
    run('git', 'fetch', '--no-tags', 'origin', 'main')
    if revision != run('git', 'rev-parse', 'FETCH_HEAD', capture=True).strip():
        raise SystemExit('Deploy only the current monorepo main revision')
    runs = json.loads(run('gh', 'run', 'list', '--repo', 'TerraScale/terrascale',
                          '--branch', 'main', '--commit', revision, '--event', 'push',
                          '--limit', '100', '--json', 'status,conclusion', capture=True))
    if not runs or any(item['status'] != 'completed' or item['conclusion'] not in ('success', 'skipped') for item in runs):
        raise SystemExit('Wait for green main before deploying')
    run(sys.executable, str(ROOT / 'scripts/sdk/check-proof.py'), 'docs', revision)
    statuses = json.loads(run('gh', 'api', '--paginate', '--slurp',
                              f'repos/TerraScale/terrascale/commits/{revision}/statuses?per_page=100', capture=True))
    release = next((item for page in statuses for item in page if item['context'] == 'docs-release/docs'), None)
    if not release or release['state'] != 'success' or release['creator']['login'] != 'MarioGK':
        raise SystemExit('Qualify this exact documentation revision on a dev host first')
    with tempfile.TemporaryDirectory(prefix='terrascale-docs-deploy-') as temporary:
        output = Path(temporary)
        run('gh', 'release', 'download', f'docs/qualified-{revision}', '--repo', 'TerraScale/terrascale', '--dir', temporary)
        manifest = json.loads((output / 'site-manifest.json').read_text())
        if manifest['revision'] != revision or manifest['sourceDirectory'] != 'docs-site':
            raise SystemExit('The artifact identifies a different source revision')
        archive = output / 'site.tar.gz'
        if hashlib.sha256(archive.read_bytes()).hexdigest() != manifest['sha256']:
            raise SystemExit('The checked site artifact hash differs')
        with tarfile.open(archive) as package:
            for member in package.getmembers():
                name = Path(member.name)
                if name.is_absolute() or '..' in name.parts or member.issym() or member.islnk():
                    raise SystemExit('Unsafe artifact path')
                prefixes = (Path('.cloudflare/output'), Path('docs/.vitepress/dist'))
                if not any(name == prefix or prefix in name.parents for prefix in prefixes):
                    raise SystemExit('Unexpected artifact member')
            for directory in (AREA / '.cloudflare/output', AREA / 'docs/.vitepress/dist'):
                if directory.exists():
                    shutil.rmtree(directory)
            package.extractall(AREA, filter='data')
    info = json.loads((AREA / 'docs/.vitepress/dist/build-info.json').read_text())
    if info['commit'] != revision:
        raise SystemExit('The built site does not identify this source revision')
    run('git', 'fetch', '--no-tags', 'origin', 'main')
    if revision != run('git', 'rev-parse', 'FETCH_HEAD', capture=True).strip():
        raise SystemExit('Main moved; deploy its newly qualified revision instead')
    run('cf', 'deploy', '--prebuilt', '--message', revision, cwd=AREA)
    run('node', 'scripts/check-hosted.mjs', cwd=AREA)
    run('gh', 'api', f'repos/TerraScale/terrascale/statuses/{revision}',
        '-f', 'state=success', '-f', 'context=docs-live/docs',
        '-f', 'description=Qualified monorepo site deployed; hosted pages and assets match',
        '-f', 'target_url=https://docs.terrascale.tech/build-info.json')
    print(f'LIVE {revision} https://docs.terrascale.tech/build-info.json')


if __name__ == '__main__':
    main()
