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
assert maintenance.count("repeat: 1") == 2 and maintenance.count("repeat: 2") == 2
assert "turso-native-build-${{ matrix.arch }}-${{ matrix.repeat }}" in maintenance
assert 'cmp "native-builds/turso-native-build-$arch-1/runtimes/$rid/native/libturso_sdk_kit.so"' in workflow
aggregate = workflow.split("\n  test:\n", 1)[1]
assert "name: ${{ github.event_name == 'workflow_dispatch' && inputs.native_artifacts && 'native-maintenance-unqualified' || 'test' }}" in aggregate
assert "if: ${{ !cancelled() && !(github.event_name == 'workflow_dispatch' && inputs.native_artifacts) }}" in aggregate
assert "needs: [changes, native, libs, auth, terrabase-build, terrabase, rust-proof]" in aggregate
assert "name: turso-native-verified" in workflow
recipe = (root / "scripts/vendor/Dockerfile.turso-native").read_text()
assert "--features turso_sdk_kit/fts" in recipe
assert "aegis-feature-tree.txt" in recipe and "(pure-rust|pure-rust-crypto)" in recipe
assert "check-turso-native-crypto.sh" in recipe
feature_patch = (root / "src/vendor/Turso.Native/patches/0002-select-runtime-aegis-c-backend.patch").read_text()
assert 'default-features = false, features = ["encryption", "fts"]' in feature_patch
portable_patch = (root / "src/vendor/Turso.Native/patches/0003-keep-aegis-c-runtime-portable.patch").read_text()
for flag in ("-mtune=native", "-mcrypto", "-mneon", "-maes"):
    assert f'-        .flag_if_supported("{flag}")' in portable_patch
assert "#define HAS_HW_AES" in portable_patch
crypto = (root / "scripts/vendor/check-turso-native-crypto.sh").read_text()
for proof in ("libaegis_aegis256_aesni_implementation", "libaegis_aegis256_neon_aes_implementation", "libaegis_aegis256_soft_implementation", "aegis256_pick_best_implementation", "objdump -d"):
    assert proof in crypto
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
    for script in ("download-turso-native.sh", "verify-turso-native.sh", "prepare-turso-native-release.sh", "check-turso-native-crypto.sh"):
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
        result = subprocess.run(["bash", str(scripts / script), *map(str, arguments)], env=env, capture_output=True, text=True, cwd=fixture if script == "check-turso-native-crypto.sh" else None)
        assert (result.returncode == 0) == succeeds, (script, result.returncode, result.stdout, result.stderr)

    # Tool fixtures exercise refusal of missing hardware, dispatch, fallback and macro evidence.
    compiler = fixture / "target/lib-release/build/aegis-fixture/output"
    compiler.parent.mkdir(parents=True)
    audit = "cargo:warning=terrascale-aegis compiler=cc -O3\ncargo:warning=terrascale-aegis HAS_HW_AES=undefined; runtime dispatch and portable fallback retained\n"
    compiler.write_text(audit)
    # Match the pinned AEGIS implementations.h names, including its libaegis_ prefix.
    symbols = "0 d libaegis_aegis256_soft_implementation\n1 t aegis256_pick_best_implementation\n2 d libaegis_aegis256_aesni_implementation\n3 d libaegis_aegis256_neon_aes_implementation\n"
    (assets / "crypto-symbols.txt").write_text(symbols)
    for tool, source in (("nm", "crypto-symbols.txt"), ("objdump", "crypto-disassembly.txt")):
        stub = fixture / tool
        stub.write_text(f'#!/bin/sh\ncat "$TEST_NATIVE_ASSETS/{source}"\n')
        stub.chmod(0o755)
    for rid, instruction in (("linux-x64", "aesenc"), ("linux-arm64", "aese")):
        (assets / "crypto-disassembly.txt").write_text(f"  100: {instruction} v0, v1\n")
        proof = fixture / f"crypto-proof-{rid}"
        run("check-turso-native-crypto.sh", "synthetic.so", rid, proof)
        hardware = "2 d libaegis_aegis256_aesni_implementation\n" if rid == "linux-x64" else "3 d libaegis_aegis256_neon_aes_implementation\n"
        for missing in ("0 d libaegis_aegis256_soft_implementation\n", "1 t aegis256_pick_best_implementation\n", hardware):
            (assets / "crypto-symbols.txt").write_text(symbols.replace(missing, ""))
            run("check-turso-native-crypto.sh", "synthetic.so", rid, proof, succeeds=False)
        (assets / "crypto-symbols.txt").write_text(symbols.replace("libaegis_", ""))
        run("check-turso-native-crypto.sh", "synthetic.so", rid, proof, succeeds=False)
        (assets / "crypto-symbols.txt").write_text(symbols)
        (assets / "crypto-disassembly.txt").write_text("  100: nop\n")
        run("check-turso-native-crypto.sh", "synthetic.so", rid, proof, succeeds=False)
        (assets / "crypto-disassembly.txt").write_text(f"  100: {instruction} v0, v1\n")
        compiler.write_text("cargo:warning=terrascale-aegis compiler=cc -maes\n")
        run("check-turso-native-crypto.sh", "synthetic.so", rid, proof, succeeds=False)
        compiler.write_text(audit)

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
        (release / f"{rid}-build-receipt.txt").write_text(f"host={host}\naegis_backend=runtime-dispatched-c\n")
        (release / f"{rid}-aegis-proof.txt").write_text(
            f"crypto proof: {rid} hardware instructions, runtime dispatch and portable fallback present; HAS_HW_AES undefined\n")
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
    (builds[1] / "release/linux-arm64-aegis-proof.txt").write_text("no hardware/fallback proof\n")
    manifest(builds[1], "SHA256SUMS-linux-arm64")
    run("prepare-turso-native-release.sh", *builds, fixture / "missing-crypto-proof", succeeds=False)
    (builds[1] / "release/linux-arm64-aegis-proof.txt").write_text(
        "crypto proof: linux-arm64 hardware instructions, runtime dispatch and portable fallback present; HAS_HW_AES undefined\n")
    (builds[1] / "release/source-receipt.txt").write_text("different build inputs\n")
    manifest(builds[1], "SHA256SUMS-linux-arm64")
    run("prepare-turso-native-release.sh", *builds, fixture / "mismatched", succeeds=False)
    (builds[1] / "release/source-receipt.txt").write_text("same pinned inputs\n")
    (builds[1] / "release/linux-arm64-contract1-smoke.txt").write_text("not qualified\n")
    manifest(builds[1], "SHA256SUMS-linux-arm64")
    run("prepare-turso-native-release.sh", *builds, fixture / "unqualified", succeeds=False)
print("turso-native-pipeline-test: ok (release-only CI, patch, download/cache hashes, ELF, pending pins and release receipts)")
