#!/usr/bin/env python3
"""Private, machine-free Tigris storage for one CI Slow run."""
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys

VARIABLES = ("AWS_ENDPOINT_URL_S3", "AWS_REGION", "BUCKET_NAME", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY")


def fly(*args):
    result = subprocess.run(["flyctl", *args], capture_output=True, text=True, check=False)
    if result.returncode:
        # Provisioning output can contain credentials, including on partial failure.
        raise RuntimeError(f"flyctl {' '.join(args[:2])} failed (exit {result.returncode}); private output withheld")
    return result.stdout


def main():
    action, app, filename = sys.argv[1:]
    if action not in ("create", "destroy") or not re.fullmatch(r"ts-ci-slow-[0-9]+-[0-9]+", app):
        raise ValueError("expected create|destroy ts-ci-slow-<run>-<attempt> <credential-file>")
    path = Path(filename)
    bucket = f"terrascale-{app}"
    if action == "create":
        fly("apps", "create", app, "-o", "terrascale-previews-515")
        output = fly("storage", "create", "-a", app, "-n", bucket, "--yes")
        values = {}
        for line in output.splitlines():
            key, separator, value = line.strip().partition(":")
            if separator and key in VARIABLES:
                values[key] = value.strip()
        if any(not values.get(key) for key in VARIABLES) or values["BUCKET_NAME"] != bucket:
            raise RuntimeError("Tigris did not return the five expected credentials for this run's bucket")
        # Exclusive creation and mode 0600: credentials never enter logs, repo files or image layers.
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, "w") as target:
            target.write("".join(f"{key}={shlex.quote(values[key])}\n" for key in VARIABLES))
        print(f"Created isolated Slow bucket {bucket}; no machines")
    else:
        path.unlink(missing_ok=True)
        apps = json.loads(fly("apps", "list", "-o", "terrascale-previews-515", "--json"))
        if not any(item["Name"] == app for item in apps):
            return
        if json.loads(fly("machines", "list", "-a", app, "--json")):
            raise RuntimeError("refusing cleanup: the Slow scratch app unexpectedly has machines")
        # flyctl storage list has no JSON option; names occupy the first table column.
        buckets = fly("storage", "list", "-o", "terrascale-previews-515")
        if any(line.split()[0] == bucket for line in buckets.splitlines() if line.split()):
            fly("storage", "destroy", bucket, "--yes")
        fly("apps", "destroy", app, "--yes")
        print(f"Deleted isolated Slow bucket/app {app}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError) as error:
        sys.exit(str(error))
