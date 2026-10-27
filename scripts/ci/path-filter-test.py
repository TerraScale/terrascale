#!/usr/bin/env python3
"""Run the workflow's real dorny matcher on isolated Git fixtures."""

import concurrent.futures
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import textwrap

workflow, action = (Path(value).resolve() for value in sys.argv[1:])
assert action.is_file(), action
match = re.search(r"^          filters: \|\n((?:^            .*\n|^\s*\n)+)", workflow.read_text(), re.MULTILINE)
assert match, "CI filters block was not found"
filters = textwrap.dedent(match[1])
areas = ("libs", "auth", "auth-server", "terrabase", "rust-proof")


def git(directory, *args):
    return subprocess.run(["git", "-C", str(directory), *args], check=True, capture_output=True, text=True)


def outputs(path):
    lines = iter(path.read_text().splitlines())
    result = {}
    for line in lines:
        key, delimiter = line.split("<<", 1)
        value = []
        for line in lines:
            if line == delimiter:
                break
            value.append(line)
        result[key] = "\n".join(value)
    return result


def check(case):
    filename, status, expected = case
    with tempfile.TemporaryDirectory(prefix="terrascale-path-filter-") as directory:
        root = Path(directory)
        repo = root / "repo"
        repo.mkdir()
        git(repo, "init", "--quiet", "--initial-branch=fixture")
        path = repo / filename
        path.parent.mkdir(parents=True, exist_ok=True)
        (repo / "baseline.txt").write_text("fixture\n")
        if status != "added":
            path.write_text("before\n")
        git(repo, "add", ".")
        git(repo, "-c", "user.name=Path filter fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "fixture")
        if status == "deleted":
            path.unlink()
        else:
            path.write_text("after\n")
            git(repo, "add", filename)
        output = root / "output"
        output.touch()
        event = root / "event.json"
        event.write_text("{}\n")
        env = {"PATH": os.environ["PATH"], "GITHUB_OUTPUT": str(output), "GITHUB_EVENT_PATH": str(event),
               "GITHUB_EVENT_NAME": "push", "GITHUB_REPOSITORY": "fixture/fixture", "GITHUB_WORKSPACE": str(root),
               "INPUT_WORKING-DIRECTORY": str(repo), "INPUT_FILTERS": filters, "INPUT_BASE": "HEAD", "INPUT_TOKEN": ""}
        run = subprocess.run(["node", str(action)], env=env, capture_output=True, text=True)
        assert run.returncode == 0, f"{filename}: matcher failed: {run.stdout} {run.stderr}"
        actual = {area for area in areas if outputs(output).get(area) == "true"}
        assert actual == expected, f"{status} {filename}: expected {sorted(expected)}, got {sorted(actual)}"
    return f"{status} {filename} -> {','.join(sorted(actual)) or 'no areas'}"


all_areas = {"libs", "auth", "terrabase"}
vendor_paths = ("src/vendor/Turso.Data.Sqlite/Connection.cs", "tests/Turso.Data.Sqlite.Tests/ConnectionTests.cs", "scripts/vendor/audit/check.sh")
cases = [(path, status, all_areas) for path in vendor_paths for status in ("added", "modified", "deleted")]
cases += [("src/vendor/Turso.Data.Sqlite/.editorconfig", "modified", all_areas),
          ("scripts/ci/path-filter-test.py", "modified", set()),
          ("scripts/ci/cache-policy-test.py", "modified", set()),
          ("scripts/ci/test-area.sh", "modified", {"libs", "terrabase"}),
          ("scripts/terrabase/libsql/rust/Cargo.lock", "modified", {"terrabase", "rust-proof"}),
          ("scripts/terrabase/libsql/rust/src/main.rs", "modified", {"terrabase", "rust-proof"}),
          ("README.md", "modified", set()),
          ("docs/auth/mfa.md", "modified", set()),
          ("docs/terrabase/ci.md", "modified", set()),
          ("docs/auth/platform-api.openapi.yaml", "modified", {"auth"}),
          ("src/TerraBase/Program.cs", "modified", {"auth-server", "terrabase"}),
          ("src/TerraScale.Authentication/Program.cs", "modified", {"auth"})]
errors = []
with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
    futures = [pool.submit(check, case) for case in cases]
    for future in concurrent.futures.as_completed(futures):
        try:
            print(future.result())
        except Exception as error:
            errors.append(str(error))
assert not errors, "\n".join(errors)
print(f"{len(cases)} path-filter fixtures passed using {action}")
