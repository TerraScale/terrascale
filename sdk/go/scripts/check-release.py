#!/usr/bin/env python3
import hashlib
import os
import subprocess
import tarfile
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
dist = root / "dist"
expected_version = os.environ.get("TS_GO_VERSION", "0.0.0-dev")
checksums = dict(line.split()[::-1] for line in (dist / "checksums.txt").read_text().splitlines())
for platform in ("linux", "darwin", "windows"):
    for arch in ("amd64", "arm64"):
        name = f"ts_{platform}_{arch}.{'zip' if platform == 'windows' else 'tar.gz'}"
        assert hashlib.sha256((dist / name).read_bytes()).hexdigest() == checksums[name], name
        if platform == "windows":
            with zipfile.ZipFile(dist / name) as archive:
                assert "ts.exe" in archive.namelist(), name
        else:
            with tarfile.open(dist / name) as archive:
                assert archive.getmember("ts").mode & 0o111, name
formula = (dist / "homebrew/Formula/ts.rb").read_text()
assert "class Ts < Formula" in formula
assert 'bin.install "ts"' in formula
assert expected_version in formula
native = dist / "ts_linux_amd64_v1/ts"
assert subprocess.check_output([native, "--version"], text=True) == f"ts {expected_version}\n"
print("Verified six archives against checksums.txt, the Homebrew formula and the version.")
