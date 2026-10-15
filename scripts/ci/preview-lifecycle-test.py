#!/usr/bin/env python3
"""Qualify late preview starts and close/build exclusion from the workflow commands."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap

root = Path(__file__).resolve().parents[2]
ci = (root / ".github/workflows/ci.yml").read_text()
preview = ci.split("\n  preview:\n", 1)[1].split("\n  deploy:\n", 1)[0]
cleanup_workflow = (root / ".github/workflows/previews.yml").read_text()
discover, cleanup = cleanup_workflow.split("\n  discover:\n", 1)[1].split("\n  cleanup:\n", 1)
assert not re.search(r"^concurrency:", cleanup_workflow, re.M), "Different PR closes must not cancel each other's cleanup"
assert "group: preview-${{ github.event.pull_request.number }}" in preview
# Every deletion, on close or by the sweep, runs in the PR's creation group and cancels a running preview build.
assert "group: preview-${{ matrix.pr }}" in cleanup and "pr: ${{ fromJSON(needs.discover.outputs.prs) }}" in cleanup
assert "cancel-in-progress: true" in cleanup, "Deletion must stop an active preview before deleting its app"
assert "fail-fast: false" in cleanup, "One PR's failed deletion must not cancel the others"
for job in (preview, discover, cleanup):
    assert "    runs-on: ${{ vars.CI_RUNNER || 'ubuntu-latest' }}\n" in job and "runs-on: alpha" not in job

# Discovery publishes what it decided even when it fails; cleanup runs only for a non-empty JSON list.
cleanup_if = (
    "!cancelled() && (github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository) "
    "&& startsWith(needs.discover.outputs.prs, '[') && needs.discover.outputs.prs != '[]'"
)
condition = re.search(r"^    if: >-\n((?:      .+\n)+)", cleanup, re.M)
assert condition and " ".join(condition[1].split()) == cleanup_if
discover_run = textwrap.dedent(discover.split("        run: |\n", 1)[1])
for event, printed, script_exit, prs, job_exit in [
        ("pull_request", "", 0, "[123]", 0), ("schedule", "[1,3]", 0, "[1,3]", 0), ("schedule", "[]", 0, "[]", 0),
        ("schedule", "[1]", 1, "[1]", 1), ("schedule", "", 1, "", 1)]:
    with tempfile.TemporaryDirectory(prefix="preview-discover-") as directory:
        path = Path(directory)
        (path / "scripts/fly").mkdir(parents=True)
        fake = path / "scripts/fly/preview.sh"
        fake.write_text(f'#!/bin/sh\ntest "$1" = expired\nprintf "%s" "{printed}"\nexit {script_exit}\n')
        fake.chmod(0o755)
        output = path / "output"
        env = os.environ | {"GITHUB_OUTPUT": str(output), "GITHUB_EVENT_NAME": event, "PR_NUMBER": "123"}
        result = subprocess.run(["bash", "--noprofile", "--norc", "-e", "-o", "pipefail", "-c", discover_run],
                                cwd=path, env=env, capture_output=True, text=True)
        assert result.returncode == job_exit, result.stderr
        assert output.read_text() == f"prs={prs}\n"

steps = re.split(r"^      - ", preview, flags=re.M)[1:]
state_step = next(step for step in steps if step.startswith("name: Read current PR lifecycle\n"))
assert "shell: bash" in state_step
command = textwrap.dedent(state_step.split("        run: |\n", 1)[1])
mutations = [step for step in steps if any(marker in step for marker in (
    "preview.sh app", "docker/build-push-action", "preview.sh machine", "gh pr comment"))]
assert len(mutations) == 5
assert all("if: steps.pull-request.outputs.state == 'open'" in step for step in mutations)

for state, api_exit in [("open", 0), ("closed", 0), ("", 22), ("unexpected", 0)]:
    with tempfile.TemporaryDirectory(prefix="preview-lifecycle-") as directory:
        path = Path(directory)
        gh = path / "gh"
        gh.write_text('#!/bin/sh\nprintf "%s\\n" "$PR_STATE"\nexit "$API_EXIT"\n')
        gh.chmod(0o755)
        output = path / "output"
        env = os.environ | {"PATH": f"{path}:{os.environ['PATH']}", "GITHUB_OUTPUT": str(output),
                            "GITHUB_REPOSITORY": "fixture/repo", "PR_NUMBER": "123",
                            "PR_STATE": state, "API_EXIT": str(api_exit)}
        result = subprocess.run(["bash", "--noprofile", "--norc", "-e", "-o", "pipefail", "-c", command],
                                env=env, capture_output=True, text=True)
        if state in ("open", "closed") and api_exit == 0:
            assert result.returncode == 0, result.stderr
            assert output.read_text() == f"state={state}\n"
        else:
            assert result.returncode != 0
            assert not output.exists(), "An API failure must not permit preview creation"

print("preview-lifecycle-test: ok (open, closed, API failure, invalid state, close/build exclusion, sweep matrix)")
