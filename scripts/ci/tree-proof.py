"""Reuse CI only for a checked-tree artifact from a successful trusted workflow.

The producer must upload last, after real checks (never on a docs-only skip),
using its actual git HEAD^{tree}. No artifact content is downloaded or executed.
"""
from datetime import datetime, timezone
import json
import os
import re
import subprocess
import sys


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True, timeout=15).stdout


def api(path):
    result = json.loads(command("gh", "api", "--hostname", "github.com", path))
    if not isinstance(result, dict):
        raise ValueError("Expected API object")
    return result


def timestamp(value):
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.utcoffset() is None:
        raise ValueError("Missing timestamp timezone")
    return result


def find_proof(repository, tree, now, fetch=api):
    if not re.fullmatch(r"[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid repository")
    if not re.fullmatch(r"[0-9a-f]{40}", tree):
        raise ValueError("Invalid tree")
    name = f"checked-tree-{tree}"
    artifacts = fetch(f"repos/{repository}/actions/artifacts?name={name}&per_page=5")["artifacts"]
    if not isinstance(artifacts, list):
        raise ValueError("Expected artifacts list")
    # shortcut: inspect five exact-name matches; widen only if retention hides valid proofs.
    for artifact in artifacts[:5]:
        if artifact["name"] != name or artifact["expired"] is not False:
            continue
        created = timestamp(artifact["created_at"])
        if not created <= now < timestamp(artifact["expires_at"]):
            continue
        run_id = artifact["workflow_run"]["id"]
        if type(run_id) is not int or run_id <= 0:
            raise ValueError("Invalid run ID")
        run = fetch(f"repos/{repository}/actions/runs/{run_id}")
        if (type(run["id"]) is int and run["id"] == run_id
                and run["repository"]["full_name"] == repository
                and run["head_commit"]["tree_id"] == tree
                and run["path"] == ".github/workflows/ci.yml"
                and run["event"] in ("pull_request", "merge_group")
                and run["status"] == "completed" and run["conclusion"] == "success"
                and timestamp(run["run_started_at"]) < created):
            # Require both actual tested tree and producer source tree; names alone are forgeable.
            return f"https://github.com/{repository}/actions/runs/{run_id}"
    return ""


def main():
    proof = ""
    try:
        tree = command("git", "rev-parse", "HEAD^{tree}").strip()
        proof = find_proof(os.environ["GITHUB_REPOSITORY"], tree, datetime.now(timezone.utc))
    except (OSError, subprocess.SubprocessError, ValueError, KeyError, TypeError, AttributeError):
        print("Tree proof unavailable; running fallback checks.", file=sys.stderr)
    # Caller appends this single safe line to GITHUB_OUTPUT.
    print(f"proof={proof}")


def self_check():
    repository, tree = "TerraScale/ts-dashboard", "a" * 40
    now = timestamp("2026-10-22T12:00:00Z")
    artifact = {"name": f"checked-tree-{tree}", "expired": False,
                "created_at": "2026-10-22T11:30:00Z", "expires_at": "2026-10-23T12:00:00Z",
                "workflow_run": {"id": 42}}
    run = {"id": 42, "repository": {"full_name": repository},
           "path": ".github/workflows/ci.yml", "event": "pull_request",
           "status": "completed", "conclusion": "success",
           "run_started_at": "2026-10-22T11:00:00Z", "head_sha": "b" * 40,
           "head_commit": {"tree_id": tree}}

    def lookup(candidate=artifact, source=run):
        calls = []

        def fetch(path):
            calls.append(path)
            if len(calls) == 1:
                assert path == f"repos/{repository}/actions/artifacts?name=checked-tree-{tree}&per_page=5"
                return {"artifacts": [candidate]}
            assert path == f"repos/{repository}/actions/runs/42"
            return source

        return find_proof(repository, tree, now, fetch)

    expected = f"https://github.com/{repository}/actions/runs/42"
    assert lookup() == expected
    assert lookup(source={**run, "event": "merge_group"}) == expected
    for change in (
        {"path": ".github/workflows/other.yml"},
        {"repository": {"full_name": "Other/repo"}},
        {"head_commit": {"tree_id": "b" * 40}},
        {"event": "push"}, {"event": "pull_request_target"},
        {"status": "in_progress"}, {"conclusion": "failure"},
        {"id": 43}, {"run_started_at": artifact["created_at"]},
    ):
        assert lookup(source={**run, **change}) == "", change
    for change in (
        {"expired": True}, {"expired": "false"},
        {"name": f"checked-tree-{'b' * 40}"},
        {"expires_at": "2026-10-22T12:00:00Z"},
        {"created_at": "2026-10-23T12:00:00Z"},
        {"created_at": "2026-10-22T10:00:00Z"},
    ):
        assert lookup(candidate={**artifact, **change}) == "", change
    for malformed in (None, {}, {"artifacts": None}, {"artifacts": [None]}):
        try:
            find_proof(repository, tree, now, lambda path: malformed)
        except (ValueError, KeyError, TypeError, AttributeError):
            pass
        else:
            raise AssertionError("Malformed response accepted")
    calls = []

    def bounded_fetch(path):
        calls.append(path)
        return {"artifacts": [artifact] * 20} if len(calls) == 1 else {**run, "event": "push"}

    assert find_proof(repository, tree, now, bounded_fetch) == ""
    assert len(calls) == 6
    print("PASS: exact tree, workflow/repository/event/status, expiry/time and bounded lookup")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-check"]:
        self_check()
    elif not sys.argv[1:]:
        main()
    else:
        raise SystemExit("usage: tree-proof.py [--self-check]")
