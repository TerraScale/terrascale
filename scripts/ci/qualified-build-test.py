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
manifest = {'schema': 'terrascale.ci-build/v1', 'revision': sha, 'area': 'terrabase', 'files': {},
            'payload': {'key': f'fast-ci/{sha}/terrabase/image-payloads.tar.gz', 'sha256': 'b' * 64}}

with tempfile.TemporaryDirectory() as scratch:
    directory = Path(scratch)
    path = directory / 'manifest.json'
    path.write_text(json.dumps(manifest))
    rows = {
        'ci-build/terrabase': {'state': 'success', 'creator': {'login': 'MarioGK'},
                             'description': 'Checked build; manifest sha256 ' + qualified.digest(path)},
        'ts-ship/terrabase': {'state': 'success', 'creator': {'login': 'MarioGK'},
                            'description': 'Fast passed in 20.1s (30s warn); test times reported; build 100s (<600s)'},
    }
    original = copy.deepcopy(rows)
    qualified.statuses = lambda revision: rows
    qualified.gh = lambda *args: ''
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
    rows['ts-ship/terrabase']['description'] = 'Fast passed in 121s (30s warn); test times reported; build 100s (<600s)'
    assert verify() == manifest  # Both budgets are warning-only until #1084 complies.
    rows = copy.deepcopy(original)
    for context in rows:
        for state in ('pending', 'failure', 'error'):
            rows[context]['state'] = state
            rejects()
        rows[context] = copy.deepcopy(original[context])
        rows[context]['creator']['login'] = 'outside-contributor'
        rejects()
        rows[context] = copy.deepcopy(original[context])
    for description in ('Fast passed in 121s (<120s); test times reported;',
                        'Fast passed in 20s (<300s); build 10s (<600s)',
                        'Fast passed in 20s (<120s); build 10s (<600s)'):
        rows['ts-ship/terrabase']['description'] = description
        rejects()
    rows = copy.deepcopy(original)
    path.write_text(json.dumps({**manifest, 'revision': 'c' * 40}))
    rejects()
    rows['ci-build/terrabase']['description'] = 'Checked build; manifest sha256 ' + qualified.digest(path)
    rejects()
    path.write_text(json.dumps({**manifest, 'payload': {'key': 'outside/image.tar.gz', 'sha256': 'b' * 64}}))
    rows['ci-build/terrabase']['description'] = 'Checked build; manifest sha256 ' + qualified.digest(path)
    rejects()
print('qualified-build-test: ok (pending/failure, actor, timing, checksum, revision and object-key boundaries)')
