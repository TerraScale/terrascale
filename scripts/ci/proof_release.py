#!/usr/bin/env python3
"""Immutable per-area assets in one qualification release per source commit."""
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
from urllib.parse import quote

REPO = 'TerraScale/terrascale'
AREAS = {'libs', 'auth', 'terrabase', 'dashboard', 'docs',
         'sdk-dotnet', 'sdk-js', 'sdk-go', 'sdk-python', 'sdk-rust'}


def gh(*args):
    for attempt in range(3):
        try:
            return subprocess.check_output(['gh', *args], text=True, stderr=subprocess.PIPE, timeout=60)
        except subprocess.CalledProcessError as error:
            if attempt == 2 or not re.search(r'HTTP\s+5\d\d\b', error.stderr or ''):
                raise
            time.sleep(2 ** attempt)


def tag_for(sha):
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('Expected exact source SHA')
    return f'ci/qualified-{sha}'


def release(tag):
    return json.loads(gh('api', f'repos/{REPO}/releases/tags/{quote(tag, safe="")}'))


def ensure_release(sha):
    tag = tag_for(sha)
    try:
        return release(tag)
    except subprocess.CalledProcessError as error:
        if not re.search(r'HTTP\s+404\b', error.stderr or ''):
            raise
    try:
        gh('release', 'create', tag, '--repo', REPO, '--target', sha, '--prerelease',
           '--title', f'CI qualified {sha}', '--notes', f'Immutable, area-namespaced dev-host qualification assets for {sha}.')
    except subprocess.CalledProcessError as error:
        # Concurrent areas may create the same release. A 5xx may also follow
        # a successful write: reconcile its identity before another mutation.
        if not re.search(r'HTTP\s+(?:5\d\d|422)\b|already exists', error.stderr or ''):
            raise
    return release(tag)


def checksum(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def download(sha, area, directory, names, legacy_tag=None):
    if area not in AREAS:
        raise ValueError('Unknown qualification area')
    tag = legacy_tag or tag_for(sha)
    destination = Path(directory)
    destination.mkdir(parents=True, exist_ok=True)
    if not names:
        names = [row['name'][len(area) + 1:] for row in release(tag)['assets']
                 if row['name'].startswith(area + '-')]
        if not names:
            raise ValueError('No assets for requested qualification area')
    for name in names:
        if Path(name).name != name or name in {'.', '..'}:
            raise ValueError('Expected plain asset filename')
        asset = name if legacy_tag else f'{area}-{name}'
        with tempfile.TemporaryDirectory(prefix='proof-download-') as scratch:
            gh('release', 'download', tag, '--repo', REPO, '--pattern', asset, '--dir', scratch, '--clobber')
            shutil.copyfile(Path(scratch) / asset, destination / name)


def publish(sha, area, paths):
    if area not in AREAS or not paths:
        raise ValueError('Expected qualification area and assets')
    tag = tag_for(sha)
    ensure_release(sha)
    with tempfile.TemporaryDirectory(prefix='proof-upload-') as scratch:
        for source in map(Path, paths):
            asset = f'{area}-{source.name}'
            staged = Path(scratch) / asset
            shutil.copyfile(source, staged)
            existing = {row['name']: row for row in release(tag)['assets']}
            if asset not in existing:
                try:
                    # Never clobber: another worker can only win with identical
                    # bytes. Its checksum is verified below before publishing status.
                    gh('release', 'upload', tag, str(staged), '--repo', REPO)
                except subprocess.CalledProcessError as error:
                    if not re.search(r'HTTP\s+(?:5\d\d|422)\b|already exists', error.stderr or ''):
                        raise
            with tempfile.TemporaryDirectory(prefix='proof-existing-') as check:
                download(sha, area, check, [source.name])
                if checksum(Path(check) / source.name) != checksum(source):
                    raise ValueError(f'Immutable qualification asset differs: {asset}')
    return f'https://github.com/{REPO}/releases/tag/{tag}'


def main():
    action, sha, area, *args = sys.argv[1:]
    if action == 'publish':
        print(publish(sha, area, args))
    elif action == 'download':
        directory, *names = args
        download(sha, area, directory, names)
    else:
        raise ValueError('Expected publish or download')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        # CalledProcessError omits stderr, avoiding signed asset URLs in logs.
        sys.exit('FAILED qualification release: ' + str(error))
