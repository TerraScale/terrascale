#!/usr/bin/env python3
"""Check workflow routing: CI areas through the workflow's real dorny matcher, every workflow's push paths, and
that no path glob in any workflow is dead (matches no tracked file)."""

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
root = workflow.parents[2]
match = re.search(r"^          filters: \|\n((?:^            .*\n|^\s*\n)+)", workflow.read_text(), re.MULTILINE)
assert match, "CI filters block was not found"
filters = textwrap.dedent(match[1])
areas = ("libs", "auth", "auth-server", "auth-client", "terrabase")
tracked = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], check=True, capture_output=True,
                         text=True).stdout.rstrip("\0").split("\0")
tracked = [path for path in tracked if (root / path).is_file()]


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


def dorny(filenames, status, rules):
    """Run the pinned action against a fixture whose working tree adds, modifies or deletes filenames."""
    with tempfile.TemporaryDirectory(prefix="terrascale-path-filter-") as directory:
        fixture = Path(directory)
        repo = fixture / "repo"
        repo.mkdir()
        git(repo, "init", "--quiet", "--initial-branch=fixture")
        (repo / "baseline.txt").write_text("fixture\n")
        for name in filenames:
            path = repo / name
            path.parent.mkdir(parents=True, exist_ok=True)
            if status != "added":
                path.write_text("before\n")
        git(repo, "add", ".")
        git(repo, "-c", "user.name=Path filter fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "fixture")
        for name in filenames:
            path = repo / name
            if status == "deleted":
                path.unlink()
            else:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("after\n")
        git(repo, "add", "--all", ".")
        output = fixture / "output"
        output.touch()
        event = fixture / "event.json"
        event.write_text("{}\n")
        env = {"PATH": os.environ["PATH"], "GITHUB_OUTPUT": str(output), "GITHUB_EVENT_PATH": str(event),
               "GITHUB_EVENT_NAME": "push", "GITHUB_REPOSITORY": "fixture/fixture", "GITHUB_WORKSPACE": str(fixture),
               "INPUT_WORKING-DIRECTORY": str(repo), "INPUT_FILTERS": rules, "INPUT_BASE": "HEAD", "INPUT_TOKEN": ""}
        run = subprocess.run(["node", str(action)], env=env, capture_output=True, text=True)
        assert run.returncode == 0, f"{filenames[:3]}: matcher failed: {run.stdout} {run.stderr}"
        return {key for key, value in outputs(output).items() if value == "true"}


def github_glob(pattern):
    """GitHub Actions filter pattern semantics: *, **, ?, + and [] (docs: filter pattern cheat sheet)."""
    regex, index = "", 0
    while index < len(pattern):
        if pattern.startswith("**/", index):
            regex, index = regex + "(?:.*/)?", index + 3
        elif pattern.startswith("**", index):
            regex, index = regex + ".*", index + 2
        elif pattern[index] == "*":
            regex, index = regex + "[^/]*", index + 1
        elif pattern[index] in "?+":
            regex, index = regex + pattern[index], index + 1
        elif pattern[index] == "[":
            end = pattern.index("]", index)
            regex, index = regex + pattern[index:end + 1], end + 1
        else:
            regex, index = regex + re.escape(pattern[index]), index + 1
    return re.compile(regex + r"\Z")


def triggers(path):
    """Each workflow's `on:` events, with branches/tags/paths lists, read from the repository's YAML layout."""
    lines = path.read_text().splitlines()
    anchors, events, event, key = {}, {}, None, None
    start = next(i for i, line in enumerate(lines) if line in ("on:", '"on":'))
    for line in lines[start + 1:]:
        if line and not line.startswith(" "):
            break
        if item := re.fullmatch(r"  ([a-z_]+):.*", line):
            event, key = item[1], None
            events[event] = {}
        elif item := re.fullmatch(r"    ([a-z-]+):(?: (.*))?", line):
            key, value = item[1], (item[2] or "").strip()
            events[event][key] = []
            if value.startswith("*"):
                events[event][key] = anchors[value[1:]]
            elif value.startswith("["):
                events[event][key] = [part.strip().strip("'\"") for part in value.strip("[]").split(",")]
            elif value.startswith("&"):
                anchors[value[1:]] = events[event][key]
        elif (item := re.fullmatch(r"      - (.*)", line)) and key:
            events[event][key].append(item[1].strip().strip("'\""))
    return events


def selected(patterns, path):
    included = False
    for pattern in patterns:
        if pattern.startswith("!"):
            included = included and not github_glob(pattern[1:]).match(path)
        elif github_glob(pattern).match(path):
            included = True
    return included


workflows = {path.name: triggers(path) for path in sorted(workflow.parent.glob("*.yml"))}


def main_push(name, path):
    """Whether a main push changing path starts this workflow (ci.yml always starts and selects areas itself)."""
    push = workflows[name].get("push")
    if push is None or "tags" in push or "main" not in push.get("branches", ["main"]):
        return False
    return "paths" not in push or selected(push["paths"], path)


def check(case):
    filename, status, expected = case[:3]
    filenames = [filename] if isinstance(filename, str) else filename
    actual = dorny(filenames, status, filters) & set(areas)
    assert actual == expected, f"{status} {filename}: expected CI areas {sorted(expected)}, got {sorted(actual)}"
    if len(case) == 4:
        started = {name for name in workflows if name != "ci.yml" and main_push(name, filename)}
        assert started == case[3], f"{filename}: expected workflows {sorted(case[3])}, got {sorted(started)}"
    return f"{status} {filename} -> {','.join(sorted(actual)) or 'no areas'}"


def live_globs():
    """A glob that matches no tracked file is a dead path: its project moved or was deleted."""
    dead = []
    for name, events in workflows.items():
        for event, keys in events.items():
            for pattern in keys.get("paths", []) + keys.get("paths-ignore", []):
                regex = github_glob(pattern.removeprefix("!"))
                if not any(regex.match(path) for path in tracked):
                    dead.append(f"{name} on.{event}.paths: {pattern}")
    globs = sorted({line.strip()[2:] for line in filters.splitlines() if line.strip().startswith("- ") and "*" != line.strip()[2]})
    rules = "".join(f"g{index}:\n  - '{glob}'\n" for index, glob in enumerate(globs))
    hit = dorny(tracked, "added", rules)
    dead += [f"ci.yml filter: {glob}" for index, glob in enumerate(globs) if f"g{index}" not in hit]
    assert not dead, "Dead workflow path globs:\n" + "\n".join(dead)
    return f"{len(globs)} CI filter globs and every workflow paths glob match tracked files"


all_areas = {"libs", "auth", "terrabase"}
terrabase = {"auth-server", "terrabase"}
sdks = {f"sdk-{lang}-ci.yml" for lang in ("dotnet", "go", "js", "python", "rust")}
go = {"sdk-go-ci.yml", "sdk-go-release.yml"}
landing = {"landing-ci.yml", "landing-secret-scan.yml"}
# The vendored driver is built into TerraBase, which auth's integration tests start; libs never builds it.
vendor_paths = (("src/vendor/Turso.Data.Sqlite/Connection.cs", terrabase), ("tests/Turso.Data.Sqlite.Tests/ConnectionTests.cs", {"terrabase"}),
                ("scripts/vendor/audit/check.sh", {"terrabase"}))
cases = [(path, status, expected) for path, expected in vendor_paths for status in ("added", "modified", "deleted")]
cases += [("src/vendor/Turso.Data.Sqlite/.editorconfig", "modified", terrabase),
          ("scripts/ci/path-filter-test.py", "modified", set()),
          ("scripts/ci/cache-policy-test.py", "modified", set()),
          ("scripts/ci/test-area.sh", "modified", {"libs", "terrabase"}),
          ("scripts/ci/auth-build.sh", "modified", {"auth"}),
          ("scripts/ci/terrabase-shard.py", "modified", {"terrabase"}),
          ("scripts/terrabase/storage-format-check.sh", "modified", {"terrabase"}),
          ("scripts/terrabase/postgres/go/main.go", "modified", {"terrabase"}),
          ("docs/auth/mfa.md", "modified", set()),
          ("docs/terrabase/ci.md", "modified", set())]
instructions = ["AGENTS.md", "CLAUDE.md", "sdk/dotnet/AGENTS.md",
                "src/TerraBase/CLAUDE.md", "src/TerraScale.Authentication/AGENTS.md",
                "src/TerraScale.Endpoints/AGENTS.md", "scripts/terrabase/CLAUDE.md",
                "docs/dev/testing.md", ".cos/brief.md"]
cases += [(instructions, "modified", set()),
          (instructions + ["src/TerraBase/Program.cs"], "modified", terrabase),
          # auth-server and auth-client together release ts-auth after TerraBase.
          (["src/TerraBase/Program.cs", "src/TerraScale.Authentication/Program.cs"], "modified",
           terrabase | {"auth", "auth-client"})]
# One change under every top-level folder and root file: the CI areas and every other workflow a main push starts.
cases += [(path, "modified", ci, started) for path, ci, started in [
    ("src/TerraBase/Program.cs", terrabase, set()),
    ("src/TerraScale.TerraBase.Contracts/Contract.cs", terrabase, {"images-ghcr.yml"}),
    ("src/TerraScale.TerraBase.Rpc/Service.cs", terrabase, set()),
    ("src/TerraScale.Authentication/Program.cs", {"auth", "auth-client"}, set()),
    ("src/TerraScale.Endpoints/Endpoint.cs", all_areas, {"images-ghcr.yml"}),
    ("src/TerraScale.Endpoints.Generators/Generator.cs", all_areas, {"images-ghcr.yml"}),
    ("src/TerraScale.Endpoints.Example/Program.cs", all_areas, set()),
    ("src/TerraScale.Logging/Log.cs", all_areas, set()),
    ("src/TerraScale.Dashboard/Program.cs", set(), {"images-ghcr.yml"}),
    ("tests/TerraBase.Fast/HealthTests.cs", {"terrabase"}, set()),
    ("tests/TerraScale.Authentication.Fast/Tests.cs", {"auth"}, set()),
    ("tests/TerraScale.Dashboard.E2E/Tests.cs", set(), {"images-ghcr.yml"}),
    ("tests/TerraScale.Endpoints.Tests/Tests.cs", {"libs"}, set()),
    ("sdk/dotnet/src/TerraScale.DotNet/Client.cs", {"auth", "auth-client", "terrabase"}, {"sdk-dotnet-ci.yml", "images-ghcr.yml"}),
    ("sdk/go/v1/client.go", set(), go),
    ("sdk/go/contracts/terrabase/sql/error-codes.json", set(), go),
    ("sdk/js/src/index.ts", set(), {"sdk-js-ci.yml"}),
    ("sdk/python/src/terrascale/client.py", set(), {"sdk-python-ci.yml"}),
    ("sdk/rust/src/lib.rs", set(), {"sdk-rust-ci.yml"}),
    ("docs/dev/testing.md", set(), set()),
    ("docs/auth/platform-api.openapi.yaml", {"auth"}, set()),
    ("docs-site/docs/index.md", set(), {"docs-ci.yml"}),
    ("landing-site/src/App.svelte", set(), landing),
    ("aspire/apphost.ts", set(), set()),
    ("benchmark/go/main.go", set(), set()),
    ("benchmark/dotnet/TerraScale.Endpoints.Benchmarks/Program.cs", {"libs"}, set()),
    ("benchmark/dotnet/DatabaseEncryption/Program.cs", set(), set()),
    ("ops/runbook.sh", set(), set()),
    ("scripts/sdk/build-release.py", set(), sdks | go),
    ("docs-site/scripts/check-main.py", set(), {"docs-ci.yml"}),
    ("scripts/dashboard/ci/release-gate.py", set(), {"images-ghcr.yml"}),
    ("scripts/auth/check.sh", {"auth"}, set()),
    ("scripts/ci/run-budget.py", set(), set()),
    ("Makefile", all_areas, {"docs-ci.yml", "images-ghcr.yml"}),
    ("Directory.Packages.props", all_areas, {"images-ghcr.yml"}),
    ("Dockerfile", all_areas, set()),
    ("README.md", set(), set()),
    (".cos/brief.md", set(), set()),
    (".github/dependabot.yml", set(), set()),
    (".github/workflows/ci.yml", all_areas, set()),
    (".github/workflows/dashboard-ci.yml", set(), {"images-ghcr.yml"}),
    (".github/workflows/docs-deploy.yml", set(), {"docs-ci.yml"}),
    (".github/workflows/landing-ci.yml", set(), landing),
    (".github/workflows/sdk-fast.yml", set(), sdks),
    (".github/workflows/sdk-go-publish.yml", set(), go),
    (".github/workflows/slow.yml", set(), set())]]
errors = []
with concurrent.futures.ThreadPoolExecutor(max_workers=os.cpu_count()) as pool:
    futures = [pool.submit(check, case) for case in cases] + [pool.submit(live_globs)]
    for future in concurrent.futures.as_completed(futures):
        try:
            print(future.result())
        except Exception as error:
            errors.append(str(error))
assert not errors, "\n".join(errors)
print(f"{len(cases)} routing fixtures passed using {action}")
