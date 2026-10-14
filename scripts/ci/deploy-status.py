#!/usr/bin/env python3
"""Fail a current push/main run unless Fast, deployment and live E2E passed.

The final workflow job must use always(), need [test, deploy, e2e], and pass
toJSON(needs) as NEEDS_JSON. Only superseded green main runs may skip deployment.
"""
import json
import os
import re
import subprocess
import sys


def check_status(current_sha, run_sha, needs):
    if not all(isinstance(sha, str) and re.fullmatch(r"[a-f0-9]{40}", sha)
               for sha in (current_sha, run_sha)):
        raise ValueError("Cannot determine current main or run SHA")
    if not isinstance(needs, dict):
        raise ValueError("NEEDS_JSON must contain job results")
    test = needs.get("test", {})
    if not isinstance(test, dict) or test.get("result") != "success":
        raise ValueError("Main quality gate test did not succeed")
    if current_sha != run_sha:
        return f"Superseded main {run_sha}; current main is {current_sha}"
    for name in ("deploy", "e2e"):
        job = needs.get(name, {})
        result = job.get("result") if isinstance(job, dict) else None
        if result != "success":
            raise ValueError(f"Current green main {run_sha}: {name} did not succeed ({result!r})")
        if name == "deploy":
            outputs = job.get("outputs", {})
            if not isinstance(outputs, dict) or outputs.get("deployed") != "true":
                raise ValueError(f"Current green main {run_sha}: deploy did not report deployed=true")
    return f"Current main {run_sha}: quality gate, deployment and live E2E passed"


def main():
    try:
        if (os.environ.get("GITHUB_EVENT_NAME") != "push"
                or os.environ.get("GITHUB_REF") != "refs/heads/main"):
            raise ValueError("Deployment status guard only accepts push/main runs")
        repository = os.environ["GITHUB_REPOSITORY"]
        current_sha = subprocess.run(
            ["gh", "api", f"repos/{repository}/git/ref/heads/main", "--jq", ".object.sha"],
            check=True, capture_output=True, text=True,
        ).stdout.strip()
        print(check_status(current_sha, os.environ["GITHUB_SHA"], json.loads(os.environ["NEEDS_JSON"])))
    except (KeyError, ValueError, OSError, subprocess.SubprocessError) as error:
        print(f"::error::Deployment status guard refused: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
