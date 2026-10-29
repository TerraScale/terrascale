#!/usr/bin/env python3
"""Exercise the publication proof gate against process-level GitHub fixtures."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

checker = Path(__file__).with_name("check-proof.py").resolve()
revision = "a" * 40
with tempfile.TemporaryDirectory(prefix="sdk-proof-test-") as temporary:
    root = Path(temporary)
    fixture = root / "statuses.json"
    fake = root / "gh"
    fake.write_text(f"#!{sys.executable}\nimport os,pathlib,sys\n"
                    f"assert sys.argv[1:] == ['api','--paginate','--slurp','repos/TerraScale/terrascale/commits/{revision}/statuses?per_page=100']\n"
                    "print(pathlib.Path(os.environ['PROOF_FIXTURE']).read_text())\n")
    fake.chmod(0o755)
    env = dict(os.environ, PATH=f"{root}:{os.environ['PATH']}", PROOF_FIXTURE=str(fixture))
    passed = 0
    for area in ("sdk-dotnet", "sdk-js", "sdk-go", "sdk-python", "sdk-rust", "docs", "dashboard"):
        for release in (False, True):
            context = f"sdk-release/{area}" if release else f"ts-ship/{area}"
            valid = {"context": context, "state": "success", "creator": {"login": "MarioGK"}, "description": "passed"}
            cases = [(True, [[valid]]), (False, [[]]),
                     (False, [[dict(valid, state="pending"), valid]]),
                     (False, [[dict(valid, state="failure"), valid]]),
                     (False, [[dict(valid, creator={"login": "untrusted"})]]),
                     (False, [[dict(valid, context="unrelated")]])]
            for expected, pages in cases:
                fixture.write_text(json.dumps(pages))
                result = subprocess.run([sys.executable, str(checker), area, revision, *(["--release"] if release else [])],
                                        env=env, text=True, capture_output=True)
                assert (result.returncode == 0) == expected, (area, release, pages, result.stdout, result.stderr)
                passed += 1
    print(f"{passed} proof cases passed: exact revision/context, trusted issuer, newest result, and missing-proof rejection")
