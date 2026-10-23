#!/usr/bin/env python3
"""Exercise pinned-download/cached-byte trust boundaries without network or Rust."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
workflow = (root / ".github/workflows/ci.yml").read_text()
native = workflow.split("\n  native:\n", 1)[1].split("\n  native-build:\n", 1)[0]
maintenance = workflow.split("\n  native-build:\n", 1)[1].split("\n  native-release:\n", 1)[0]
assert "build-turso-native.sh" not in native and "download-turso-native.sh" in native
assert "if: github.event_name == 'workflow_dispatch' && inputs.native_artifacts" in maintenance
assert "ubuntu-24.04-arm" in maintenance
aggregate = workflow.split("\n  test:\n", 1)[1]
assert "name: ${{ github.event_name == 'workflow_dispatch' && inputs.native_artifacts && 'native-maintenance-unqualified' || 'test' }}" in aggregate
assert "if: ${{ !cancelled() && !(github.event_name == 'workflow_dispatch' && inputs.native_artifacts) }}" in aggregate
assert "needs: [changes, native, libs, auth, terrabase]" in aggregate
assert "name: turso-native-verified" in workflow
recipe = (root / "scripts/vendor/Dockerfile.turso-native").read_text()
assert "--features turso_sdk_kit/fts" in recipe and "--no-default-features" not in recipe
patch = root / "src/vendor/Turso.Native/patches/0001-preserve-page-authentication-damage.patch"
assert hashlib.sha256(patch.read_bytes()).hexdigest() == "eadb14ec88ac351c938908443bcb89da324411e4eb8f8d068b3cc755fc0808ae"

with tempfile.TemporaryDirectory(prefix="native-pipeline-") as temporary:
    fixture = Path(temporary)
    scripts = fixture / "scripts/vendor"
    scripts.mkdir(parents=True)
    vendor = fixture / "src/vendor/Turso.Native"
    vendor.mkdir(parents=True)
    assets = fixture / "assets"
    assets.mkdir()
    for script in ("download-turso-native.sh", "verify-turso-native.sh", "prepare-turso-native-release.sh"):
        shutil.copy2(root / "scripts/vendor" / script, scripts)
    # Minimal synthetic ELF headers verify architecture checks, never pretend to be loadable libraries.
    hashes = []
    for rid, machine in (("linux-x64", b"\x3e\x00"), ("linux-arm64", b"\xb7\x00")):
        content = b"\x7fELF\x02\x01" + bytes(12) + machine + bytes(44)
        name = f"terrascale-turso-{rid}.so"
        (assets / name).write_bytes(content)
        hashes.append(f"{hashlib.sha256(content).hexdigest()}  {name}\n")
    pins = vendor / "SHA256SUMS"
    pins.write_text("".join(hashes))
    curl = fixture / "curl"
    curl.write_text("#!/usr/bin/env python3\nimport os,pathlib,shutil,sys\na=sys.argv\nurl=a[a.index('--tlsv1.2')+1]\nshutil.copyfile(pathlib.Path(os.environ['TEST_NATIVE_ASSETS'])/url.rsplit('/',1)[1], a[a.index('-o')+1])\n")
    curl.chmod(0o755)
    env = {**os.environ, "PATH": f"{fixture}:{os.environ['PATH']}", "TEST_NATIVE_ASSETS": str(assets)}
    output = fixture / "native"

    def run(script, *arguments, succeeds=True):
        result = subprocess.run(["bash", str(scripts / script), *map(str, arguments)], env=env, capture_output=True, text=True)
        assert (result.returncode == 0) == succeeds, (script, result.returncode, result.stdout, result.stderr)

    run("download-turso-native.sh", output)
    run("verify-turso-native.sh", output)
    library = output / "runtimes/linux-x64/native/libturso_sdk_kit.so"
    original = library.read_bytes()
    library.write_bytes(original + b"tampered")
    run("verify-turso-native.sh", output, succeeds=False)
    library.write_bytes(original)
    pins.write_text(hashes[1])
    run("verify-turso-native.sh", output, succeeds=False)
    run("download-turso-native.sh", output, succeeds=False)
    # Matching SHA alone must not admit a binary for the wrong CPU.
    wrong = (assets / "terrascale-turso-linux-arm64.so").read_bytes()
    library.write_bytes(wrong)
    pins.write_text(f"{hashlib.sha256(wrong).hexdigest()}  terrascale-turso-linux-x64.so\n" + hashes[1])
    run("verify-turso-native.sh", output, succeeds=False)
    pins.write_text("".join(hashes))
    (assets / "terrascale-turso-linux-x64.so").write_bytes(original + b"tampered")
    run("download-turso-native.sh", output, succeeds=False)
    # Release assembly must retain matching source receipts and refuse incomplete smoke evidence.
    builds = []
    for rid, host in (("linux-x64", "x86_64"), ("linux-arm64", "aarch64")):
        build = fixture / rid
        release = build / "release"
        release.mkdir(parents=True)
        runtime = build / f"runtimes/{rid}/native/libturso_sdk_kit.so"
        runtime.parent.mkdir(parents=True)
        runtime.write_bytes(original if rid == "linux-x64" else wrong)
        (release / "source-receipt.txt").write_text("same pinned inputs\n")
        (release / f"{rid}-build-receipt.txt").write_text(f"host={host}\n")
        (release / f"{rid}-fts-smoke.txt").write_text(
            "native smoke: candidate query/cursor/sort/preflight/count use indexed text id seeks; ULIDs, missing hits and empty counts passed\n"
            "native smoke: three-term carrying-slop reader/count/ordered queries refuse scratch; small admitted slop succeeds\n")
        (release / f"{rid}-contract1-smoke.txt").write_text(
            "contract1 smoke: marker=1, aegis256 persisted FTS, SQL spoof constraint and page tamper TURSO_CORRUPT passed\n")

        def manifest(directory, name):
            files = sorted(path for path in directory.rglob("*") if path.is_file() and path.name != name)
            (directory / name).write_text("".join(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(directory)}\n" for path in files))

        manifest(build, f"SHA256SUMS-{rid}")
        builds.append(build)
    prepared = fixture / "prepared"
    run("prepare-turso-native-release.sh", *builds, prepared)
    assert (prepared / "SHA256SUMS").is_file() and (prepared / "NATIVE-SHA256SUMS").read_text() == "".join(reversed(hashes))
    (builds[1] / "release/source-receipt.txt").write_text("different build inputs\n")
    manifest(builds[1], "SHA256SUMS-linux-arm64")
    run("prepare-turso-native-release.sh", *builds, fixture / "mismatched", succeeds=False)
    (builds[1] / "release/source-receipt.txt").write_text("same pinned inputs\n")
    (builds[1] / "release/linux-arm64-contract1-smoke.txt").write_text("not qualified\n")
    manifest(builds[1], "SHA256SUMS-linux-arm64")
    run("prepare-turso-native-release.sh", *builds, fixture / "unqualified", succeeds=False)
print("turso-native-pipeline-test: ok (release-only CI, patch, download/cache hashes, ELF, pending pins and release receipts)")
