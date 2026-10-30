#!/usr/bin/env python3
"""Exercise the workflow's actual Slow reporter without contacting GitHub."""

import concurrent.futures
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap

workflow = Path(__file__).resolve().parents[2] / ".github/workflows/ci.yml"
workflow_text = workflow.read_text()
match = re.search(r"      - name: Report the Slow result\n.*?        run: \|\n(.*?)(?=\n      - |\n  [a-z][a-z0-9-]*:|\Z)", workflow_text, re.DOTALL)
assert match, "Slow report block not found"
report_header = match[0].split("        run: |", 1)[0]
assert "if: ${{ always() }}" in report_header, "Report setup/build failures too"
assert "continue-on-error:" not in report_header, "A failed Slow suite must make its job red"
record_start = workflow_text.index("      - name: Record the actual scheduled suite inputs\n")
upload_start = workflow_text.index("      - name: Retain last actual scheduled suite inputs\n")
assert match.end() <= record_start < upload_start, "Record suite inputs only after reporting"
for start in (record_start, upload_start):
    marker_header = workflow_text[start:].split("\n      - ", 1)[0]
    assert "steps.slow-report.outputs.completed == 'true'" in marker_header, "Markers require completed reporting"
required = workflow_text.split("\n  test:\n", 1)[1]
needs = re.search(r"^    needs: \[(.+)\]$", required, re.M)
assert needs and not any("slow" in job for job in needs[1].split(",")), "Slow must not block required test"
assert workflow_text.count("steps: *slow-steps") == 3, "All four Slow jobs must share the reporter"
script = "set -euo pipefail\n" + textwrap.dedent(match[1])
script = script.replace("${{ env.AREA }}", "auth").replace("${{ github.job }}", "main")
assert "${{" not in script, "Unhandled workflow expression"


def check(case):
    name, code, tests, main, expected, completed, api_failure = case
    with tempfile.TemporaryDirectory(prefix="terrascale-slow-report-") as directory:
        root = Path(directory)
        bin_path = root / "bin"
        bin_path.mkdir()
        gh = bin_path / "gh"
        gh.write_text("#!/usr/bin/env python3\n" + textwrap.dedent('''\
            import json, os, sys
            from pathlib import Path
            args = sys.argv[1:]
            with Path(os.environ["CALLS"]).open("a") as output:
                output.write(json.dumps(args) + "\\n")
            if os.environ["FAIL_ISSUE_CREATE"] == "true" and args[:2] == ["issue", "create"]:
                raise SystemExit(73)
            if args[:2] == ["issue", "list"]:
                print(json.dumps([{"number": 42, "title": "Slow: case_0"}]))
        '''))
        gh.chmod(0o755)
        if code is not None:
            slow = root / "slow"
            slow.mkdir()
            (slow / "exit").write_text(f"{code}\n")
            log = "".join(f"failed case_{index} (1ms)\n  Exception: Bearer fixture-credential secret=fixture-secret\n" for index in range(tests))
            (slow / "log").write_text(log + "Test run summary: fixture Authentication.Slow\n  total: 25\n")
        calls_path = root / "calls"
        output_path = root / "output"
        env = {**os.environ, "PATH": f"{bin_path}:{os.environ['PATH']}", "CALLS": str(calls_path),
               "GITHUB_OUTPUT": str(output_path), "FAIL_ISSUE_CREATE": str(api_failure).lower(),
               "GITHUB_STEP_SUMMARY": str(root / "summary"), "GITHUB_REPOSITORY": "fixture/fixture",
               "GITHUB_SHA": "fixture-sha", "RUN_URL": "https://example.invalid/run",
               "GITHUB_REF": "refs/heads/main" if main else "refs/heads/fixture"}
        run = subprocess.run(["bash", "-c", script], cwd=root, env=env, capture_output=True, text=True)
        expected_code = 73 if api_failure else code if code is not None else 1
        assert run.returncode == expected_code, f"{name}: {run.returncode}: {run.stderr}"
        outputs = output_path.read_text().splitlines() if output_path.exists() else []
        assert outputs == (["completed=true"] if completed else []), f"{name}: unexpected outputs {outputs}"
        marker_eligible = main and "completed=true" in outputs and (root / "slow/exit").is_file()
        assert marker_eligible == completed, f"{name}: unexpected marker eligibility"
        calls = [json.loads(line) for line in calls_path.read_text().splitlines()] if calls_path.exists() else []
        reports = [args for args in calls if args[:2] in (["issue", "create"], ["issue", "comment"])]
        assert len(reports) == expected, f"{name}: expected {expected} reports, got {len(reports)}"
        for args in reports:
            body = args[args.index("--body") + 1]
            assert "fixture-credential" not in body and "fixture-secret" not in body, f"{name}: credentials leaked"
            assert "fixture-sha" in body and "https://example.invalid/run" in body
            assert "Reproduce:" in body and "Expected:" in body and "Actual:" in body
        if main and tests:
            assert any(args[:3] == ["issue", "comment", "42"] for args in reports), f"{name}: did not update existing issue"
        if code is None:
            assert "did not finish building" in (root / "summary").read_text()
        return name


cases = [("passed", 0, 0, True, 0, True, False),
         ("failures-and-redaction", 2, 2, True, 2, True, False),
         ("all-eleven-failures", 2, 11, True, 11, True, False),
         ("missing-build-output", None, 0, True, 1, False, False),
         ("branch-summary-only", 2, 2, False, 0, False, False),
         ("issue-api-failure-after-partial-report", 2, 3, True, 2, False, True)]
with concurrent.futures.ThreadPoolExecutor(max_workers=len(cases)) as pool:
    for result in pool.map(check, cases):
        print(f"Slow report: {result} passed")
