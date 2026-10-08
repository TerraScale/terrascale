#!/usr/bin/env python3
"""Process-level actor harness regressions; all output and fault injection stay temporary."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--dotnet", required=True)
parser.add_argument("--terrabase-root", required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
source = Path(args.terrabase_root).resolve()
assembly = root / "actor-harness/bin/Release/net11.0/ActorHarness.dll"
hook = root / "tests/actor-harness/bin/Release/net11.0/ActorHarnessChecks.dll"


def run(command, env=None, timeout=20):
    return subprocess.run(
        command, cwd=root, env=env, capture_output=True, text=True, timeout=timeout
    )


def require(condition, result):
    if not condition:
        raise AssertionError(f"{result.args}\n{result.stdout}\n{result.stderr}")


with tempfile.TemporaryDirectory(prefix="ts648-actor-check-") as directory:
    temporary = Path(directory)
    capture = temporary / "launch.json"
    fake_dotnet = temporary / "dotnet"
    fake_dotnet.write_text(
        f"#!{sys.executable}\n"
        "import json, os, pathlib, sys\n"
        "capture = pathlib.Path(os.environ['ACTOR_CHECK_CAPTURE'])\n"
        "record = json.dumps({"
        "'args': sys.argv[1:], 'source': os.environ.get('ACTOR_SOURCE_REVISION'),"
        "'harness': os.environ.get('ACTOR_HARNESS_REVISION')})\n"
        "capture.write_text(record)\n"
        "with capture.with_suffix('.jsonl').open('a') as history: history.write(record + '\\n')\n"
    )
    fake_dotnet.chmod(0o700)
    environment = dict(os.environ, ACTOR_CHECK_CAPTURE=str(capture))
    relative = os.path.relpath(source, root)
    make = ["make", f"DOTNET={fake_dotnet}", f"TERRABASE_ROOT={relative}"]
    spaced_source = temporary / "Terra Base"
    result = run(["git", "clone", "--shared", "--no-checkout", "--quiet", str(source), str(spaced_source)])
    require(result.returncode == 0, result)
    for supplied, expected in (
        (relative, source),
        (str(spaced_source), spaced_source),
        (os.path.relpath(spaced_source, root), spaced_source),
    ):
        invocation = ["make", f"DOTNET={fake_dotnet}", f"TERRABASE_ROOT={supplied}"]
        history = capture.with_suffix(".jsonl")
        history.unlink(missing_ok=True)
        result = run([*invocation, f"ACTOR_PYTHON={fake_dotnet}", "actor-check"], environment)
        require(result.returncode == 0, result)
        launches = [json.loads(line) for line in history.read_text().splitlines()]
        require(f"-p:TerraBaseRoot={expected}" in launches[0]["args"], result)
        require(f"--terrabase-root={expected}" in launches[-1]["args"], result)
        result = run([*invocation, "actor-run"], environment)
        require(result.returncode == 0, result)
        launched = json.loads(capture.read_text())
        require(f"-p:TerraBaseRoot={expected}" in launched["args"], result)
        require(bool(launched["source"]) and bool(launched["harness"]), result)
    print("PASS: both targets preserve relative and absolute checkout paths containing spaces")

    capture.unlink()
    result = run(
        ["make", "actor-run", f"DOTNET={fake_dotnet}", f"TERRABASE_ROOT={temporary}"],
        environment,
    )
    require(result.returncode != 0 and not capture.exists(), result)
    print("PASS: source Git lookup failure prevents launch")

    real_git = shutil.which("git")
    fake_git = temporary / "git"
    fake_git.write_text(
        f"#!{sys.executable}\n"
        "import os, sys\n"
        "if '-C' not in sys.argv: sys.exit(1)\n"
        f"os.execv({real_git!r}, [{real_git!r}, *sys.argv[1:]])\n"
    )
    fake_git.chmod(0o700)
    result = run(
        [*make, "actor-run"],
        dict(environment, PATH=f"{temporary}{os.pathsep}{environment['PATH']}"),
    )
    require(result.returncode != 0 and not capture.exists(), result)
    print("PASS: harness Git lookup failure prevents launch")

    smoke = [
        args.dotnet, str(assembly), "--operations=32", "--iterations=0",
        "--repeats=1", "--keys=8", "--actors=1",
    ]
    fault = [*smoke, "--trial-timeout-seconds=1"]
    clean_environment = dict(os.environ)
    clean_environment.pop("DOTNET_STARTUP_HOOKS", None)
    clean_environment.update(ACTOR_SOURCE_REVISION="", ACTOR_HARNESS_REVISION=" ")
    receipt = temporary / "normal.md"
    # Three successful trials each allow 30 seconds plus cleanup; timeout tests retain their short bounds.
    result = run([*smoke, "--trial-timeout-seconds=30", f"--output={receipt}"], clean_environment, timeout=120)
    require(result.returncode == 0, result)
    text = receipt.read_text()
    require("TerraBase revision: unrecorded" in text, result)
    require("Harness revision: unrecorded" in text, result)
    require(sum(line.startswith("| 1 |") for line in text.splitlines()) == 3, result)
    result = run([*smoke, f"--output={receipt}"], clean_environment)
    require(result.returncode != 0 and receipt.read_text() == text, result)
    print("PASS: empty revisions are unrecorded; completed trials persist; receipt reuse is refused")

    result = run([*smoke, "--trial-timeout-seconds=0"], clean_environment)
    require(result.returncode != 0 and "ArgumentOutOfRangeException" in result.stderr, result)
    print("PASS: invalid trial deadline is refused")

    initialization_receipt = temporary / "initialization.md"
    initialization_cleanup = temporary / "initialization-cleanup.txt"
    result = run(
        [*fault, f"--output={initialization_receipt}"],
        dict(clean_environment, DOTNET_STARTUP_HOOKS=str(hook),
             ACTOR_CHECK_INITIALIZATION_CLEANUP=str(initialization_cleanup)),
    )
    require(result.returncode != 0 and "actor initialization fault" in result.stderr, result)
    require("FAILED (InvalidOperationException)" in initialization_receipt.read_text(), result)
    require(initialization_cleanup.exists(), result)
    require(initialization_cleanup.read_text() == "terrabase.actors.rejected", result)
    print("PASS: router initialization failure records FAILED and disposes its provider-owned meter")

    blocked_receipt = temporary / "blocked-initialization.md"
    blocked_cleanup = temporary / "blocked-cleanup.txt"
    result = run(
        [*fault, f"--output={blocked_receipt}"],
        dict(clean_environment, DOTNET_STARTUP_HOOKS=str(hook),
             ACTOR_CHECK_INITIALIZATION_CLEANUP=str(blocked_cleanup),
             ACTOR_CHECK_INITIALIZATION_BLOCK="forever"),
    )
    require(result.returncode != 0, result)
    failure = blocked_receipt.read_text()
    require("FAILED (" in failure and "CanceledException" in failure and "TimeoutException" in failure, result)
    require(not blocked_cleanup.exists(), result)
    print("PASS: permanently blocked initialization records cancellation and timeout; exits within 20 seconds")

    late_receipt = temporary / "late-initialization.md"
    late_cleanup = temporary / "late-cleanup.txt"
    command = [*fault, f"--output={late_receipt}"]
    with subprocess.Popen(
        command, cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, text=True,
        env=dict(clean_environment, DOTNET_STARTUP_HOOKS=str(hook),
                 ACTOR_CHECK_INITIALIZATION_CLEANUP=str(late_cleanup),
                 ACTOR_CHECK_INITIALIZATION_BLOCK="stdin"),
    ) as process:
        guard = threading.Timer(20, process.kill)
        guard.start()
        try:
            errors = []
            released = False
            for line in process.stderr:
                errors.append(line)
                if line.strip() == "Actor trial deadline expired; waiting for cleanup.":
                    process.stdin.write("\n")
                    process.stdin.flush()
                    released = True
                    break
            stdout, stderr = process.communicate(timeout=20)
            result = subprocess.CompletedProcess(command, process.returncode, stdout, "".join(errors) + stderr)
            require(released and result.returncode != 0, result)
        finally:
            guard.cancel()
            if process.poll() is None:
                process.kill()
    require("FAILED (" in late_receipt.read_text() and "CanceledException" in late_receipt.read_text(), result)
    require(late_cleanup.exists() and late_cleanup.read_text() == "terrabase.actors.rejected", result)
    print("PASS: initialization released after deadline disposes its provider and persists cancellation")

    stalled_receipt = temporary / "stalled.md"
    result = run(
        [*fault, f"--output={stalled_receipt}"],
        dict(clean_environment, DOTNET_STARTUP_HOOKS=str(hook)),
    )
    require(result.returncode != 0, result)
    failure = stalled_receipt.read_text()
    require("FAILED (" in failure and "CanceledException" in failure and "TimeoutException" in failure, result)
    require("AggregateException" in result.stderr and "CanceledException" in result.stderr and "TimeoutException" in result.stderr, result)
    print("PASS: stalled real consumer preserves trial cancellation and disposal timeout; exits within 20 seconds")
