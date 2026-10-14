#!/usr/bin/env python3
"""Execute the live E2E workflow commands with passing/failing test processes."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap

workflow = Path(__file__).resolve().parents[2] / ".github/workflows/ci.yml"
job = workflow.read_text().split("\n  e2e:\n", 1)[1]
steps = dict(re.findall(r"^      - name: ([^\n]+)\n(.*?)(?=^      - |\Z)", job, re.M | re.S))
names = ["Six-direction replication matrix against Fly (gru, sjc, fra)", "Replication e2e against Fly"]

for name in names:
    step = steps[name]
    run = re.search(r"^        run: ([^\n]*)\n(.*)", step, re.M | re.S)
    assert run is not None, name
    command = textwrap.dedent(run[2]) if run[1] == "|" else run[1]
    # GitHub's implicit Bash uses -e; explicit `shell: bash` also enables pipefail.
    shell = ["bash", "--noprofile", "--norc", "-e"]
    if re.search(r"^        shell: bash$", step, re.M):
        shell += ["-o", "pipefail"]
    for make_exit, broken_log, build_exit, credential_exit in [
        (17, False, 0, 0), (0, False, 0, 0), (0, True, 0, 0),
        (0, False, 21, 0), (0, False, 0, 23),
    ]:
        with tempfile.TemporaryDirectory(prefix="e2e-status-") as directory:
            root = Path(directory)
            tools = root / "tools"
            tools.mkdir()
            credential = root / "scripts/e2e/ci-oidc-credential.sh"
            credential.parent.mkdir(parents=True)
            credential.write_text('test -f build-ran || return 98\ntouch credential-attempted\n'
                'if [ "$E2E_CREDENTIAL_EXIT" != 0 ]; then return "$E2E_CREDENTIAL_EXIT"; fi\ntouch credential-minted\n')
            for tool, script in {
                "make": '#!/bin/sh\nif [ "$1" = terrabase-build ]; then touch build-ran; exit "$E2E_BUILD_EXIT"; fi\n'
                    'touch tests-ran\ntest -f credential-minted || exit 99\necho "synthetic E2E output"\nexit "$E2E_TEST_EXIT"\n',
                "jq": '#!/bin/sh\ntouch jq-ran\nexit 0\n',
            }.items():
                path = tools / tool
                path.write_text(script)
                path.chmod(0o755)
            logfile = "replication-matrix.log" if name == names[0] else "replication-e2e.log"
            if broken_log:
                (root / logfile).mkdir()
            env = os.environ | {"PATH": f"{tools}:{os.environ['PATH']}", "E2E_TEST_EXIT": str(make_exit),
                "E2E_BUILD_EXIT": str(build_exit), "E2E_CREDENTIAL_EXIT": str(credential_exit)}
            result = subprocess.run(shell + ["-c", command], cwd=root, env=env, capture_output=True, text=True)
            expected = build_exit or credential_exit or make_exit or (1 if broken_log else 0)
            assert result.returncode == expected, (name, make_exit, broken_log, result.returncode, result.stderr)
            prepared = build_exit == 0 and credential_exit == 0
            assert (root / "build-ran").exists(), name
            assert (root / "credential-attempted").exists() == (build_exit == 0), name
            assert (root / "credential-minted").exists() == prepared, name
            assert (root / "tests-ran").exists() == prepared, name
            if prepared and not broken_log:
                assert "synthetic E2E output" in (root / logfile).read_text(), name
            elif not prepared:
                assert not (root / logfile).exists(), name
            if name == names[0]:
                assert (root / "jq-ran").exists() == (expected == 0), name

assert not re.search(r"^    continue-on-error: true$", job, re.M), "E2E failures must fail the run"
assert "if: ${{ !cancelled() }}" in steps[names[1]], "The full suite must run after a matrix failure"
assert "if: always()" in steps["Preserve replication proof and per-direction lag"], "Keep failure evidence"
print("e2e-status-test: ok (build/credential failures, test failures, successful tests, log failures and failure artifacts)")
