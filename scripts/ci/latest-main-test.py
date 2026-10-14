#!/usr/bin/env python3
"""Exercise stale-main rejection after the same-run Fast gate."""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
workflow = (root / ".github/workflows/ci.yml").read_text()
concurrency = workflow.split("\nconcurrency:\n", 1)[1].split("\npermissions:", 1)[0]
assert "group: ${{ github.workflow }}-${{ github.event_name }}-${{ github.ref }}" in concurrency
assert "cancel-in-progress: true" in concurrency
deploy = workflow.split("\n  deploy:\n", 1)[1].split("\n  e2e:\n", 1)[0]
steps = dict(re.findall(r"^      - name: ([^\n]+)\n(.*?)(?=^      - |\Z)", deploy, re.M | re.S))
gate = steps["Require current main before rollout"]
command = gate.split("        run: |\n", 1)[1].strip().replace("terrascale/", f"{root}/")
initial = steps["Skip a superseded main before building"].split("        run: ", 1)[1].strip().replace("terrascale/", f"{root}/")
assert "needs: [images, test]" in deploy
assert "fast-gate.sh" not in workflow
assert "if: steps.qualified.outputs.current == 'true'" in steps["Deploy TerraBase (gru, sjc, fra)"]
assert "if: steps.qualified.outputs.current == 'true'" in steps["Deploy ts-auth (gru, sjc, fra)"]
assert "deployed: ${{ steps.deploy-auth.outcome == 'success' }}" in deploy
assert "if: needs.deploy.outputs.deployed == 'true'" in workflow.split("\n  e2e:\n", 1)[1]

sha, other = "a" * 40, "b" * 40
prefix = "repos/TerraScale/terrascale"
base = {
    f"{prefix}/git/ref/heads/main": sha,
}
cases = [
    ("current", "current", {}, 0, "current=true\n"),
    ("stale rerun", "current", {f"{prefix}/git/ref/heads/main": other}, 0, "current=false\n"),
    ("API failure", "current", {f"{prefix}/git/ref/heads/main": None}, 1, ""),
    ("invalid response", "current", {f"{prefix}/git/ref/heads/main": "null"}, 1, ""),
    ("current after Fast", "gate", {}, 0, "current=true\n"),
    ("superseded during Fast", "gate", {f"{prefix}/git/ref/heads/main": other}, 0, "current=false\n"),
    ("API failure after Fast", "gate", {f"{prefix}/git/ref/heads/main": None}, 1, ""),
    ("invalid response after Fast", "gate", {f"{prefix}/git/ref/heads/main": "null"}, 1, ""),
]
for name, action, changes, code, output in cases:
    with tempfile.TemporaryDirectory(prefix="latest-main-") as directory:
        scratch = Path(directory)
        gh = scratch / "gh"
        gh.write_text("#!/usr/bin/env python3\nimport json, os, sys\n"
                      "value = json.loads(os.environ['GH_RESPONSES'])[sys.argv[2]]\n"
                      "if value is None: sys.exit(1)\nprint(value)\n")
        gh.chmod(0o755)
        result_file = scratch / "output"
        env = os.environ | {
            "PATH": f"{scratch}:{os.environ['PATH']}", "GH_RESPONSES": json.dumps(base | changes),
            "GITHUB_OUTPUT": str(result_file), "GITHUB_SHA": sha, "GITHUB_REPOSITORY": "TerraScale/terrascale",
        }
        cmd = command if action == "gate" else initial
        result = subprocess.run(["bash", "-e", "-c", cmd], env=env, capture_output=True, text=True)
        assert result.returncode == code, (name, result.returncode, result.stderr)
        assert (result_file.read_text() if result_file.exists() else "") == output, name

print(f"latest-main-test: ok ({len(cases)} freshness and API failure controls, same-run Fast dependency)")
