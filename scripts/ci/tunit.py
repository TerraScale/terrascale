#!/usr/bin/env python3
import os
import re
import shlex
import subprocess
import sys

FORBIDDEN = re.compile(r"[\s\[\]/|()*?\\]")

argv = sys.argv[1:]
separator = argv.index("--")
default_minimum = argv[0]
command = argv[separator + 1:]
trailing = []
if "--" in command:
    separator = command.index("--")
    command, trailing = command[:separator], command[separator + 1:]

names = os.environ.get("TESTS", "").split()
extra = shlex.split(os.environ.get("TEST_ARGS", ""))
if names and extra:
    sys.exit("pass either TESTS or TEST_ARGS, not both")


def run(runner_args):
    completed = subprocess.run(command + ["--"] + runner_args + trailing)
    if completed.returncode:
        sys.exit(completed.returncode)


if names:
    for name in names:
        if "." not in name or FORBIDDEN.search(name):
            sys.exit(f"TESTS entries must be plain Class.Method test names: {name}")
        _, _, method = name.rpartition(".")
        class_name = name.split(".")[-2]
        run(["--treenode-filter", f"/*/*/{class_name}/{method}", "--minimum-expected-tests", "1"])
    raise SystemExit(0)

if "--minimum-expected-tests" not in extra:
    extra = ["--minimum-expected-tests", default_minimum] + extra
run(extra)
