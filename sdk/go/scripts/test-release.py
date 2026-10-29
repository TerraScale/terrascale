#!/usr/bin/env python3
"""Credential-free checks against GoReleaser's snapshot output."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile

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

with tempfile.TemporaryDirectory() as directory:
    tmp = Path(directory)
    mocks = tmp / "mocks"
    mocks.mkdir()
    checksum_file = tmp / "checksums.txt"
    shutil.copyfile(dist / "checksums.txt", checksum_file)
    (mocks / "uname").write_text('#!/bin/sh\ncase "$1" in -s) echo "$TEST_OS";; -m) echo "$TEST_ARCH";; esac\n')
    (mocks / "curl").write_text("#!" + sys.executable + "\n" + '''import json, os, pathlib, shutil, sys
args = sys.argv[1:]
url = next(arg for arg in args if arg.startswith("https://"))
with open(os.environ["TEST_REQUESTS"], "a") as log:
    log.write(url + "\\n")
if url.startswith("https://api.github.com/repos/TerraScale/terrascale/releases?per_page=100&page="):
    page = int(url.rsplit("=", 1)[1])
    if str(page) == os.environ.get("TEST_FAIL_RELEASE_PAGE"):
        sys.exit(22)
    if page == 1:
        releases = [{"tag_name": f"sdk/js/qualified-{index:040x}"} for index in range(100)]
    elif page == 2:
        releases = [{"tag_name": tag} for tag in ("sdk/go/v0.0.11", "sdk/go/v0.0.9", "history/ts-go/v0.0.1")]
    elif page == 3:
        releases = [{"tag_name": tag} for tag in ("sdk/go/v0.0.12", "sdk/go/v0.0.0013", "sdk/go/v0.0.99-beta")]
    else:
        assert page == 4, page
        releases = []
    print(json.dumps(releases), end="")
else:
    assert any(tag in url for tag in ("/download/sdk/go/v0.0.12/", "/download/sdk/go/v0.0.11/", "/download/history/ts-go/v0.0.1/", "/download/history/ts-go/v0.0.10/")), url
    name = url.rsplit("/", 1)[1]
    source = pathlib.Path(os.environ["TEST_DIST"]) / name
    if name == "checksums.txt":
        source = pathlib.Path(os.environ["TEST_CHECKSUMS"])
    shutil.copyfile(source, args[args.index("-o") + 1])
''')
    for mock in mocks.iterdir():
        mock.chmod(0o755)
    home = tmp / "home"
    requests = tmp / "requests"
    env = {**os.environ, "HOME": str(home), "PATH": f"{mocks}:{os.environ['PATH']}",
           "TEST_DIST": str(dist), "TEST_CHECKSUMS": str(checksum_file),
           "TEST_REQUESTS": str(requests)}

    def install(system, arch, *args, failed_page=""):
        return subprocess.run(["sh", root / "scripts/install.sh", *args],
                              env={**env, "TEST_OS": system, "TEST_ARCH": arch, "TEST_FAIL_RELEASE_PAGE": failed_page},
                              text=True, capture_output=True)

    binary = home / ".local/bin/ts"
    for system, machine, platform, arch in (("Linux", "x86_64", "linux", "amd64"),
                                           ("Linux", "aarch64", "linux", "arm64"),
                                           ("Darwin", "amd64", "darwin", "amd64"),
                                           ("Darwin", "arm64", "darwin", "arm64")):
        result = install(system, machine)
        assert result.returncode == 0, result.stderr
        assert "Installed ts 0.0.12 " in result.stdout, result.stdout
        with tarfile.open(dist / f"ts_{platform}_{arch}.tar.gz") as archive:
            assert binary.read_bytes() == archive.extractfile("ts").read()
        assert binary.stat().st_mode & 0o111
    for page in range(1, 5):
        url = f"https://api.github.com/repos/TerraScale/terrascale/releases?per_page=100&page={page}"
        assert requests.read_text().splitlines().count(url) == 4
    original = binary.read_bytes()
    result = install("Linux", "x86_64", failed_page="3")
    assert result.returncode != 0, "failed release-page request accepted"
    assert binary.read_bytes() == original, "pagination failure changed existing installation"
    result = install("Linux", "x86_64", "0.0.1")
    assert result.returncode == 0, result.stderr
    original = binary.read_bytes()
    for version in ("0.0.10", "0.0.11"):
        result = install("Linux", "x86_64", version)
        assert result.returncode == 0, result.stderr
        assert binary.read_bytes() == original
    for bad in ("", "0" * 64 + "  ts_linux_amd64.tar.gz\n"):
        checksum_file.write_text(bad)
        result = install("Linux", "x86_64", "0.0.1")
        assert result.returncode != 0, "bad checksum accepted"
        assert binary.read_bytes() == original, "existing installation changed"
    for system, arch, version in (("Windows", "x86_64", "0.0.1"),
                                  ("Linux", "riscv64", "0.0.1"),
                                  ("Linux", "x86_64", "../bad")):
        assert install(system, arch, version).returncode != 0
        assert binary.read_bytes() == original
print("Verified six archives, version, Homebrew formula, installer platforms and checksum failures.")
