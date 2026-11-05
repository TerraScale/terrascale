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

# Real Git histories exercise docs inheritance, including Actions' shallow clone.
import os
import subprocess

original_cwd = Path.cwd()
with tempfile.TemporaryDirectory(prefix='ci-inherited-proof-test-') as scratch:
    root = Path(scratch)
    repository = root / 'repository'
    repository.mkdir()
    os.chdir(repository)
    def git(*args):
        return subprocess.check_output(['git', *args], text=True, stderr=subprocess.PIPE).strip()
    git('init', '--quiet', '--initial-branch=main')
    git('config', 'user.name', 'Receipt fixture')
    git('config', 'user.email', 'fixture@example.invalid')
    def commit(path, contents):
        file = Path(path)
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(contents)
        git('add', '--', path)
        git('commit', '--quiet', '-m', 'fixture')
        return git('rev-parse', 'HEAD')
    base = commit('src/service.cs', 'checked input\n')
    # Minimal real project graphs let the classifier prove independent code.
    for area, project in {'libs': 'src/TerraScale.Logging/Logging.csproj',
                          'auth': 'src/TerraScale.Authentication/Auth.csproj',
                          'terrabase': 'src/TerraBase/TerraBase.csproj'}.items():
        file = Path(project)
        file.parent.mkdir(parents=True, exist_ok=True)
        extra = '<ItemGroup><Compile Include="../../benchmark/dotnet/LocalBenchmarkHost.cs" /></ItemGroup>' if area == 'terrabase' else ''
        file.write_text('<Project>' + extra + '</Project>')
        Path(area + '.slnf').write_text(json.dumps({'solution': {'projects': [project]}}))
    git('add', '.')
    git('commit', '--quiet', '-m', 'fixture project graphs')
    base = git('rev-parse', 'HEAD')
    receipt = {**manifest, 'revision': base}
    payload = json.dumps(receipt)
    import hashlib
    accepted = copy.deepcopy(original)
    accepted['ci-build/terrabase']['description'] = 'Checked build; manifest sha256 ' + hashlib.sha256(payload.encode()).hexdigest()
    by_revision = {base: accepted}
    lookups, downloads = [], []
    def statuses(revision):
        lookups.append(revision)
        return by_revision.get(revision, {})
    def gh(*args):
        assert args[:2] == ('release', 'download')
        downloads.append(args[2])
        target = Path(args[args.index('--dir') + 1])
        (target / 'manifest.json').write_text(payload)
        return ''
    qualified.statuses, qualified.gh = statuses, gh
    def full_verify(revision, area='terrabase'):
        with tempfile.TemporaryDirectory(dir=root) as output, contextlib.redirect_stdout(io.StringIO()):
            source, selected = qualified.proof_source(area, revision)
            return qualified.verify(area, source, Path(output), rows=selected)
    def must_reject(revision, area='terrabase'):
        try:
            full_verify(revision, area)
        except ValueError:
            return
        raise AssertionError('Inherited proof bypassed source or trust boundary')
    first = commit('benchmark/bench/results.md', 'first measured report\n')
    second = commit('docs/ci.md', 'documentation-only descendant\n')
    assert full_verify(second) == receipt
    assert lookups[-3:] == [second, first, base]
    assert downloads[-1] == f'ci/qualified-{base}-terrabase'
    # A target's pending/failing/untrusted/incomplete proof cannot be hidden.
    for context in accepted:
        for state in ('pending', 'failure', 'error'):
            row = copy.deepcopy(accepted[context])
            row['state'] = state
            by_revision[second] = {context: row}
            lookups.clear()
            must_reject(second)
            assert lookups == [second]
    by_revision.pop(second)
    by_revision[first] = copy.deepcopy(accepted)
    by_revision[first]['ci-build/terrabase']['creator']['login'] = 'outside-contributor'
    must_reject(second)
    by_revision.pop(first)
    # A digest-valid receipt for the wrong source still fails source binding.
    payload = json.dumps({**receipt, 'revision': second})
    accepted['ci-build/terrabase']['description'] = 'Checked build; manifest sha256 ' + hashlib.sha256(payload.encode()).hexdigest()
    must_reject(second)
    payload = json.dumps(receipt)
    accepted['ci-build/terrabase']['description'] = 'Checked build; manifest sha256 ' + hashlib.sha256(payload.encode()).hexdigest()
    shallow = root / 'shallow'
    git('clone', '--quiet', '--depth=1', repository.as_uri(), str(shallow))
    os.chdir(shallow)
    assert git('rev-list', '--count', 'HEAD') == '1'
    assert full_verify(second) == receipt
    assert int(git('rev-list', '--count', 'HEAD')) > 1
    os.chdir(repository)
    for path in ('src/service.cs', 'tests/fixture.md', 'scripts/ci/qualified-build.py',
                 '.github/workflows/ci.yml', 'sdk/dotnet/README.md',
                 'benchmark/dotnet/LocalBenchmarkHost.cs', 'docs/terrabase/contract.yaml', 'Directory.Build.props'):
        git('checkout', '--quiet', '--detach', second)
        changed = commit(path, 'changed executable or test input\n')
        tip = commit('docs/after.md', 'docs after source edit\n')
        must_reject(tip)
    # Reverting source is still a source-changing edge, not documentation.
    reverted = commit('Directory.Build.props', 'different input again\n')
    must_reject(reverted)
    git('checkout', '--quiet', '--detach', second)
    left = commit('docs/left.md', 'left\n')
    git('checkout', '--quiet', '--detach', second)
    commit('docs/right.md', 'right\n')
    git('merge', '--quiet', '--no-ff', left, '-m', 'fixture merge')
    must_reject(git('rev-parse', 'HEAD'))
    # TerraBase-only changes must reuse Libraries proof (8811ba597 regression).
    libs_receipt = {**receipt, 'area': 'libs'}
    payload = json.dumps(libs_receipt)
    libs_rows = {name.replace('/terrabase', '/libs'): copy.deepcopy(row) for name, row in accepted.items()}
    libs_rows['ci-build/libs']['description'] = 'Checked build; manifest sha256 ' + hashlib.sha256(payload.encode()).hexdigest()
    by_revision = {base: libs_rows}
    for path in ('src/TerraBase/HomePush.cs', 'tests/TerraBase.Fast/Example.cs',
                 'src/TerraScale.Authentication/Auth.cs', 'src/TerraScale.Dashboard/Page.cs',
                 'benchmark/cmd/region-sim/api.go', 'sdk/go/client.go', 'docs-site/content.md'):
        git('checkout', '--quiet', '--detach', second)
        tip = commit(path, 'independent area input\n')
        assert full_verify(tip, 'libs') == libs_receipt
    for path in ('src/TerraScale.Logging/Logging.cs', 'libs.slnf', 'Directory.Build.props',
                 'sdk/dotnet/client.cs', 'scripts/ci/test-area.sh', 'src/Unknown/Input.cs',
                 'benchmark/dotnet/TerraScale.Endpoints.Benchmarks/Example.cs'):
        git('checkout', '--quiet', '--detach', second)
        tip = commit(path, 'shared or Libraries input\n')
        must_reject(tip, 'libs')
    # A future project reference makes that dependency an input automatically.
    git('checkout', '--quiet', '--detach', second)
    dependency = commit('src/TerraScale.Logging/Logging.csproj',
                        '<Project><ItemGroup><ProjectReference Include="../TerraBase/TerraBase.csproj" /></ItemGroup></Project>')
    by_revision[dependency] = libs_rows
    changed = commit('src/TerraBase/HomePush.cs', 'newly linked Libraries dependency\n')
    must_reject(changed, 'libs')
    os.chdir(original_cwd)
print('qualified-build-test: area inputs, transitive project references and linked benchmark helpers: ok')
print('qualified-build-test: docs-only ancestry, shallow checkout, source changes, merges and newer-result precedence: ok')

# An observed HTTP500 during the 8811 ancestry lookup must not turn CI red.
from unittest.mock import Mock, patch
import urllib.error
for _ in range(5):
    for failure in (urllib.error.HTTPError('https://api.github.com/fixture', 504, 'gateway', {}, None),
                    subprocess.CalledProcessError(1, ['gh'], stderr='gh: HTTP 500')):
        operation = Mock(side_effect=[failure, 'trusted metadata'])
        with patch.object(qualified.time, 'sleep') as pause, contextlib.redirect_stderr(io.StringIO()):
            assert qualified.status_read(operation) == 'trusted metadata'
            assert operation.call_count == 2
            pause.assert_called_once_with(1)
for code, count in ((503, 3), (403, 1), (404, 1)):
    operation = Mock(side_effect=urllib.error.HTTPError('https://api.github.com/fixture', code, 'failed', {}, None))
    with patch.object(qualified.time, 'sleep'), contextlib.redirect_stderr(io.StringIO()):
        try:
            qualified.status_read(operation)
        except urllib.error.HTTPError:
            pass
        else:
            raise AssertionError('Status transport failure accepted')
    assert operation.call_count == count
print('qualified-build-test: five HTTP/gh metadata recoveries; bounded 5xx and immediate 4xx rejection: ok')
