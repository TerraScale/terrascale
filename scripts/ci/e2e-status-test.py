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
    for make_exit, broken_log in [(17, False), (0, False), (0, True)]:
        with tempfile.TemporaryDirectory(prefix="e2e-status-") as directory:
            root = Path(directory)
            tools = root / "tools"
            tools.mkdir()
            for tool, script in {
                "make": '#!/bin/sh\necho "synthetic E2E output"\nexit "$E2E_TEST_EXIT"\n',
                "jq": '#!/bin/sh\ntouch jq-ran\nexit 0\n',
            }.items():
                path = tools / tool
                path.write_text(script)
                path.chmod(0o755)
            logfile = "replication-matrix.log" if name == names[0] else "replication-e2e.log"
            if broken_log:
                (root / logfile).mkdir()
            env = os.environ | {"PATH": f"{tools}:{os.environ['PATH']}", "E2E_TEST_EXIT": str(make_exit)}
            result = subprocess.run(shell + ["-c", command], cwd=root, env=env, capture_output=True, text=True)
            expected = 17 if make_exit else (1 if broken_log else 0)
            assert result.returncode == expected, (name, make_exit, broken_log, result.returncode, result.stderr)
            if not broken_log:
                assert "synthetic E2E output" in (root / logfile).read_text(), name
            if name == names[0]:
                assert (root / "jq-ran").exists() == (expected == 0), name

assert not re.search(r"^    continue-on-error: true$", job, re.M), "E2E failures must fail the run"
assert "if: ${{ !cancelled() }}" in steps[names[1]], "The full suite must run after a matrix failure"
assert "if: always()" in steps["Preserve replication proof and per-direction lag"], "Keep failure evidence"
print("e2e-status-test: ok (test failures, successful tests, log failures and failure artifacts)")
