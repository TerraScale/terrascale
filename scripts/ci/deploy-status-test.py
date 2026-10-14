#!/usr/bin/env python3
"""Exercise final main deployment status, including fail-closed GitHub API reads."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

script = Path(__file__).with_name("deploy-status.py")
sha, other = "a" * 40, "b" * 40
green = {
    "test": {"result": "success"},
    "deploy": {"result": "success", "outputs": {"deployed": "true"}},
    "e2e": {"result": "success"},
}
cases = [("deployed", sha, green, 0, 0, "deployment and live E2E passed", {})]
for job in ("deploy", "e2e"):
    for result in ("", "skipped", "cancelled", "failure"):
        needs = green | {job: {"result": result}}
        cases.append((f"{job} {result}", sha, needs, 0, 1, f"{job} did not succeed", {}))
        cases.append((f"superseded {job} {result}", other, needs, 0, 0, "Superseded main", {}))
    cases.append((f"missing {job}", sha, {k: v for k, v in green.items() if k != job},
                  0, 1, f"{job} did not succeed", {}))
    cases.append((f"malformed {job}", sha, green | {job: None},
                  0, 1, f"{job} did not succeed", {}))
for output in ({}, {"deployed": "false"}, {"deployed": True}, None):
    needs = green | {"deploy": {"result": "success", "outputs": output}}
    cases.append((f"invalid output {output}", sha, needs, 0, 1, "deployed=true", {}))
    cases.append((f"superseded output {output}", other, needs, 0, 0, "Superseded main", {}))
for result in ("", "skipped", "cancelled", "failure"):
    for head in (sha, other):
        cases.append((f"quality {result} {head}", head, green | {"test": {"result": result}},
                      0, 1, "quality gate", {}))
cases += [
    ("missing quality", sha, {}, 0, 1, "quality gate", {}),
    ("malformed needs", sha, [], 0, 1, "job results", {}),
    ("invalid JSON", sha, "broken", 0, 1, "refused", {}),
    ("invalid current SHA", "null", green, 0, 1, "SHA", {}),
    ("empty current SHA", "", green, 0, 1, "SHA", {}),
    ("invalid run SHA", other, green, 0, 1, "SHA", {"GITHUB_SHA": ""}),
    ("API failure", sha, green, 1, 1, "refused", {}),
    ("API failure obsolete", other, green, 1, 1, "refused", {}),
    ("PR event", sha, green, 0, 1, "push/main", {"GITHUB_EVENT_NAME": "pull_request"}),
    ("dispatch event", sha, green, 0, 1, "push/main", {"GITHUB_EVENT_NAME": "workflow_dispatch"}),
    ("other branch", sha, green, 0, 1, "push/main", {"GITHUB_REF": "refs/heads/feature"}),
]

with tempfile.TemporaryDirectory(prefix="deploy-status-") as directory:
    scratch = Path(directory)
    gh = scratch / "gh"
    gh.write_text("#!/usr/bin/env python3\nimport os, sys\n"
                  "assert sys.argv[1:] == ['api', 'repos/TerraScale/terrascale/git/ref/heads/main', "
                  "'--jq', '.object.sha']\n"
                  "print(os.environ['MAIN_SHA'])\nsys.exit(int(os.environ['API_EXIT']))\n")
    gh.chmod(0o755)
    for name, head, needs, api_exit, code, message, changes in cases:
        env = os.environ | {
            "PATH": f"{scratch}:{os.environ['PATH']}", "MAIN_SHA": head, "API_EXIT": str(api_exit),
            "GITHUB_EVENT_NAME": "push", "GITHUB_REF": "refs/heads/main", "GITHUB_SHA": sha,
            "GITHUB_REPOSITORY": "TerraScale/terrascale",
            "NEEDS_JSON": needs if isinstance(needs, str) else json.dumps(needs),
        } | changes
        result = subprocess.run(["python3", str(script)], env=env, capture_output=True, text=True)
        assert result.returncode == code, (name, result.stdout, result.stderr)
        assert message in (result.stderr if code else result.stdout), (name, result.stdout, result.stderr)
        assert ("::error::" in result.stderr) == bool(code), name

print(f"deploy-status-test: ok ({len(cases)} deployment, supersession, quality and API cases)")
