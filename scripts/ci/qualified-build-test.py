#!/usr/bin/env python3
"""Exercise receipt trust and source binding with local GH contract fixtures."""
import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import tempfile

spec = importlib.util.spec_from_file_location('qualified', Path(__file__).with_name('qualified-build.py'))
qualified = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qualified)
sha = 'a' * 40
manifest = {'schema': 'terrascale.ci-checks/v2', 'revision': sha, 'area': 'terrabase', 'files': {'checks.log': 'b' * 64}}

with tempfile.TemporaryDirectory() as scratch:
    directory = Path(scratch)
    path = directory / 'manifest.json'
    path.write_text(json.dumps(manifest))
    rows = {
        'ci-build/terrabase': {'state': 'success', 'creator': {'login': 'MarioGK'},
                             'description': 'Checked build; manifest sha256 ' + qualified.digest(path)},
        'ts-ship/terrabase': {'state': 'success', 'creator': {'login': 'MarioGK'},
                            'description': 'Fast passed in 20.1s (<30s; tests <=10s); test times reported; build 100s (<600s)'},
    }
    original = copy.deepcopy(rows)
    qualified.statuses = lambda revision: rows
    qualified.gh = lambda *args: ''
    qualified.download_proof = lambda *args, **kwargs: None
    def verify():
        with contextlib.redirect_stdout(io.StringIO()):
            return qualified.verify('terrabase', sha, directory)
    def rejects():
        try:
            verify()
        except ValueError:
            return
        raise AssertionError('Untrusted receipt accepted')
    assert verify() == manifest
    for elapsed in ('0', '29.999'):
        rows['ts-ship/terrabase']['description'] = f'Fast passed in {elapsed}s (<30s; tests <=10s); test times reported; build 100s (<600s)'
        assert verify() == manifest
    for elapsed in ('30', '120', '121'):
        rows['ts-ship/terrabase']['description'] = f'Fast passed in {elapsed}s (<30s; tests <=10s); test times reported; build 100s (<600s)'
        rejects()
    rows = copy.deepcopy(original)
    for context in rows:
        for state in ('pending', 'failure', 'error'):
            rows[context]['state'] = state
            rejects()
        rows[context] = copy.deepcopy(original[context])
        rows[context]['creator']['login'] = 'outside-contributor'
        rejects()
        rows[context] = copy.deepcopy(original[context])
    for description in ('Fast passed in 20s (30s warn); test times reported; build 100s (<600s)',
                        'Fast passed in 121s (<120s); test times reported;',
                        'Fast passed in 20s (<300s); build 10s (<600s)',
                        'Fast passed in 20s (<120s); build 10s (<600s)'):
        rows['ts-ship/terrabase']['description'] = description
        rejects()
    rows = copy.deepcopy(original)
    path.write_text(json.dumps({**manifest, 'revision': 'c' * 40}))
    rejects()
    rows['ci-build/terrabase']['description'] = 'Checked build; manifest sha256 ' + qualified.digest(path)
    rejects()
    path.write_text(json.dumps({**manifest, 'files': {'runtime.tar.gz': 'b' * 64}}))
    rows['ci-build/terrabase']['description'] = 'Checked build; manifest sha256 ' + qualified.digest(path)
    rejects()
print('qualified-build-test: ok (pending/failure, actor, timing, checksum, revision and log-only artifact boundaries)')


# Bounded retries cover transient server errors; authorization and missing assets
# fail immediately. Mock the backoff so repeated proof stays below Fast budgets.
from unittest.mock import patch
import subprocess
# Reload the real downloader separately because trust fixtures replace gh above.
retry_spec = importlib.util.spec_from_file_location('qualified_retry', Path(__file__).with_name('qualified-build.py'))
retry_module = importlib.util.module_from_spec(retry_spec)
retry_spec.loader.exec_module(retry_module)
for _ in range(5):
    failures = [subprocess.CalledProcessError(1, ['gh'], stderr=f'HTTP {code}: upstream unavailable') for code in (504, 502)]
    with patch.object(retry_module.subprocess, 'check_output', side_effect=[*failures, 'downloaded']) as request, patch.object(retry_module.time, 'sleep') as pause, contextlib.redirect_stderr(io.StringIO()):
        assert retry_module.gh('release', 'download', 'pinned-tag') == 'downloaded'
        assert request.call_count == 3
        assert [call.args for call in pause.call_args_list] == [(1,), (2,)]
        assert request.call_args.args[0][-1] == '--clobber'
for code, expected in ((503, 3), (404, 1), (403, 1)):
    error = subprocess.CalledProcessError(1, ['gh'], stderr=f'HTTP {code}: rejected')
    with patch.object(retry_module.subprocess, 'check_output', side_effect=error) as request, patch.object(retry_module.time, 'sleep'), contextlib.redirect_stderr(io.StringIO()):
        try:
            retry_module.gh('release', 'download', 'pinned-tag')
        except subprocess.CalledProcessError:
            pass
        else:
            raise AssertionError('Persistent download failure accepted')
        assert request.call_count == expected
print('qualified-build-test: five transient 5xx recoveries, retry exhaustion and permanent 4xx boundaries: ok')

# The release is shared, but assets remain immutable and area-bound. Exercise
# simultaneous-create reconciliation and upload ambiguity without live writes.
import proof_release as store
for _ in range(5):
    conflict = subprocess.CalledProcessError(1, ['gh'], stderr='HTTP 422: already exists')
    with patch.object(store, 'release', side_effect=[subprocess.CalledProcessError(1, ['gh'], stderr='HTTP 404'), {'id': 1}]), patch.object(store, 'gh', side_effect=conflict):
        assert store.ensure_release(sha) == {'id': 1}
with tempfile.TemporaryDirectory() as scratch:
    root = Path(scratch)
    source = root / 'manifest.json'
    source.write_text('source-bound bytes')
    uploaded = {}
    def request(*args):
        if args[:2] == ('release', 'upload'):
            staged = Path(args[3])
            uploaded[staged.name] = staged.read_bytes()
            # Server committed the asset before returning a transient error.
            raise subprocess.CalledProcessError(1, ['gh'], stderr='HTTP 502')
        if args[:2] == ('release', 'download'):
            name = args[args.index('--pattern') + 1]
            destination = Path(args[args.index('--dir') + 1])
            (destination / name).write_bytes(uploaded[name])
            return ''
        raise AssertionError(args)
    with patch.object(store, 'ensure_release'), patch.object(store, 'release', return_value={'assets': []}), patch.object(store, 'gh', side_effect=request):
        for area in ('libs', 'auth', 'terrabase', 'docs', 'dashboard', 'sdk-go'):
            assert store.publish(sha, area, [source]).endswith('ci/qualified-' + sha)
        assert len(uploaded) == 6
    with patch.object(store, 'ensure_release'), patch.object(store, 'release', return_value={'assets': [{'name': 'libs-manifest.json'}]}), patch.object(store, 'gh', side_effect=request):
        source.write_text('different bytes')
        try:
            store.publish(sha, 'libs', [source])
        except ValueError:
            pass
        else:
            raise AssertionError('Existing immutable asset overwritten')
print('qualified-build-test: shared per-commit release, concurrent creation, ambiguous upload and immutable area assets: ok')
