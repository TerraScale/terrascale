#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
verifier="${repository_root}/scripts/verify-package-set-candidate.py"
test_root="$(mktemp -d)"
trap 'rm -r "${test_root}"' EXIT

expect_failure() {
  local output_file="${test_root}/failure-$RANDOM.log"
  set +e
  "$@" >"${output_file}" 2>&1
  local status=$?
  set -e
  if [[ "${status}" -eq 0 ]]; then
    echo "expected command to fail: $*" >&2
    cat "${output_file}" >&2
    exit 1
  fi
}

if [[ "$#" -eq 2 && "$1" == "--handoff" ]]; then
  handoff_path="$2"
  mapfile -t handoff_values < <(python3 - "${handoff_path}" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    handoff = json.load(stream)
packages = {package["id"]: package for package in handoff["packages"]}
for package_id in ("TerraScale.DotNet", "TerraScale.EntityFrameworkCore"):
    print(packages[package_id]["path"])
    print(packages[package_id]["sha256"])
print(handoff["source"]["revision"])
PY
  )
  if [[ "${#handoff_values[@]}" -ne 5 ]]; then
    echo "package-set handoff fixture did not contain the expected fields" >&2
    exit 1
  fi
  handoff_directory="$(cd "$(dirname "${handoff_path}")" && pwd)"
  sdk_package="${handoff_directory}/${handoff_values[0]}"
  sdk_digest="${handoff_values[1]}"
  ef_package="${handoff_directory}/${handoff_values[2]}"
  ef_digest="${handoff_values[3]}"
  source_revision="${handoff_values[4]}"
elif [[ "$#" -eq 0 ]]; then
  package_root="${test_root}/candidate"
  mkdir -p "${package_root}"
  source_revision="$(git -C "${repository_root}" rev-parse HEAD)"
  dotnet pack "${repository_root}/src/TerraScale.DotNet/TerraScale.DotNet.csproj" \
    --configuration Release \
    --no-restore \
    --property:RepositoryCommit="${source_revision}" \
    --output "${package_root}" >/dev/null
  dotnet pack "${repository_root}/src/TerraScale.EntityFrameworkCore/TerraScale.EntityFrameworkCore.csproj" \
    --configuration Release \
    --no-restore \
    --property:RepositoryCommit="${source_revision}" \
    --output "${package_root}" >/dev/null
  sdk_package="${package_root}/TerraScale.DotNet.0.1.0.nupkg"
  ef_package="${package_root}/TerraScale.EntityFrameworkCore.0.1.0.nupkg"
  sdk_digest="$(sha256sum "${sdk_package}" | awk '{print $1}')"
  ef_digest="$(sha256sum "${ef_package}" | awk '{print $1}')"
else
  echo "usage: $0 [--handoff path/to/package-set-handoff.json]" >&2
  exit 2
fi

verification_command=(
  python3 -B "${verifier}"
  --sdk-package "${sdk_package}"
  --sdk-sha256 "${sdk_digest}"
  --ef-package "${ef_package}"
  --ef-sha256 "${ef_digest}"
  --source-revision "${source_revision}"
)

receipt_path="${test_root}/package-set-receipt.json"
if [[ -n "${handoff_path:-}" ]]; then
  TERRASCALE_PACKAGE_RECEIPT_PATH="${receipt_path}" \
    python3 -B "${verifier}" --handoff "${handoff_path}" >/dev/null
else
  TERRASCALE_PACKAGE_RECEIPT_PATH="${receipt_path}" "${verification_command[@]}" >/dev/null
fi
python3 - "${receipt_path}" "${source_revision}" "${sdk_digest}" "${ef_digest}" <<'PY'
import json
import sys

path, revision, sdk_digest, ef_digest = sys.argv[1:]
with open(path, encoding="utf-8") as stream:
    receipt = json.load(stream)
if set(receipt) != {
    "consumer",
    "dependencyClosure",
    "format",
    "handoff",
    "packages",
    "source",
    "verified",
    "vulnerabilityAudit",
}:
    raise SystemExit("package-set receipt contains unexpected fields")
if receipt["format"] != "TerraScale.DotNet.PackageSetVerificationReceipt/v2" or receipt["verified"] is not True:
    raise SystemExit("package-set receipt does not report a verified v2 package set")
packages = {package["id"]: package for package in receipt["packages"]}
if set(packages) != {"TerraScale.DotNet", "TerraScale.EntityFrameworkCore"}:
    raise SystemExit("package-set receipt does not contain exactly both package identities")
if packages["TerraScale.DotNet"]["sha256"] != sdk_digest:
    raise SystemExit("package-set receipt SDK digest mismatch")
if packages["TerraScale.EntityFrameworkCore"]["sha256"] != ef_digest:
    raise SystemExit("package-set receipt EF digest mismatch")
if receipt["source"]["revision"] != revision:
    raise SystemExit("package-set receipt source revision mismatch")
handoff = receipt["handoff"]
if receipt["source"]["ref"]:
    if not isinstance(handoff, dict) or handoff.get("format") != "TerraScale.DotNet.PackageSetHandoff/v2":
        raise SystemExit("protected package-set receipt does not bind its handoff")
    if handoff.get("name") != "terrascale-dotnet-package-set-handoff.json":
        raise SystemExit("protected package-set receipt binds an unexpected handoff name")
    if len(handoff.get("sha256", "")) != 64:
        raise SystemExit("protected package-set receipt lacks a handoff digest")
elif handoff is not None:
    raise SystemExit("direct package-set receipt invented a handoff")
closure = receipt["dependencyClosure"]
if not closure or any(item["license"] != "MIT" for item in closure):
    raise SystemExit("package-set receipt lacks the accepted dependency-license disposition")
closure_ids = {item["id"] for item in closure}
if not {"TerraScale.DotNet", "TerraScale.EntityFrameworkCore"}.issubset(closure_ids):
    raise SystemExit("package-set receipt dependency closure omits a first-party package")
audit = receipt["vulnerabilityAudit"]
if audit != {
    "enabled": True,
    "level": "low",
    "mode": "all",
    "warningsAsErrors": ["NU1900", "NU1901", "NU1902", "NU1903", "NU1904"],
}:
    raise SystemExit("package-set receipt vulnerability audit is not fail-closed")
PY

zero_digest="$(printf '0%.0s' {1..64})"
expect_failure python3 -B "${verifier}" \
  --sdk-package "${sdk_package}" --sdk-sha256 "${zero_digest}" \
  --ef-package "${ef_package}" --ef-sha256 "${ef_digest}" \
  --source-revision "${source_revision}"
expect_failure python3 -B "${verifier}" \
  --sdk-package "${sdk_package}" --sdk-sha256 "${sdk_digest}" \
  --ef-package "${ef_package}" --ef-sha256 "${zero_digest}" \
  --source-revision "${source_revision}"
expect_failure python3 -B "${verifier}" \
  --sdk-package "${ef_package}" --sdk-sha256 "${ef_digest}" \
  --ef-package "${sdk_package}" --ef-sha256 "${sdk_digest}" \
  --source-revision "${source_revision}"

linked_sdk="${test_root}/linked-sdk.nupkg"
linked_ef="${test_root}/linked-ef.nupkg"
ln -s "${sdk_package}" "${linked_sdk}"
ln -s "${ef_package}" "${linked_ef}"
expect_failure python3 -B "${verifier}" \
  --sdk-package "${linked_sdk}" --sdk-sha256 "${sdk_digest}" \
  --ef-package "${ef_package}" --ef-sha256 "${ef_digest}" \
  --source-revision "${source_revision}"
expect_failure python3 -B "${verifier}" \
  --sdk-package "${sdk_package}" --sdk-sha256 "${sdk_digest}" \
  --ef-package "${linked_ef}" --ef-sha256 "${ef_digest}" \
  --source-revision "${source_revision}"

spoofed_ef="${test_root}/spoofed-ef.nupkg"
python3 - "${ef_package}" "${spoofed_ef}" <<'PY'
import sys
import zipfile

source, target = sys.argv[1:]
with zipfile.ZipFile(source) as source_zip, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as target_zip:
    for entry in source_zip.infolist():
        contents = source_zip.read(entry.filename)
        if entry.filename == "TerraScale.EntityFrameworkCore.nuspec":
            text = contents.decode("utf-8-sig")
            expected = '<dependency id="TerraScale.DotNet" version="0.1.0"'
            if expected not in text:
                raise SystemExit("EF dependency fixture could not find the SDK dependency")
            text = text.replace(expected, '<dependency id="TerraScale.DotNet" version="0.1.1"', 1)
            contents = text.encode("utf-8")
        target_zip.writestr(entry, contents)
PY
spoofed_ef_digest="$(sha256sum "${spoofed_ef}" | awk '{print $1}')"
expect_failure python3 -B "${verifier}" \
  --sdk-package "${sdk_package}" --sdk-sha256 "${sdk_digest}" \
  --ef-package "${spoofed_ef}" --ef-sha256 "${spoofed_ef_digest}" \
  --source-revision "${source_revision}"

secret_ef="${test_root}/secret-ef.nupkg"
python3 - "${ef_package}" "${secret_ef}" <<'PY'
import sys
import zipfile

source, target = sys.argv[1:]
with zipfile.ZipFile(source) as source_zip, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as target_zip:
    for entry in source_zip.infolist():
        contents = source_zip.read(entry.filename)
        if entry.filename == "lib/net10.0/TerraScale.EntityFrameworkCore.xml":
            contents += b"\ntsauth_forbidden_package_set_fixture\n"
        target_zip.writestr(entry, contents)
PY
secret_ef_digest="$(sha256sum "${secret_ef}" | awk '{print $1}')"
expect_failure python3 -B "${verifier}" \
  --sdk-package "${sdk_package}" --sdk-sha256 "${sdk_digest}" \
  --ef-package "${secret_ef}" --ef-sha256 "${secret_ef_digest}" \
  --source-revision "${source_revision}"

unpublished_sdk="${test_root}/unpublished-sdk.nupkg"
python3 - "${sdk_package}" "${unpublished_sdk}" <<'PY'
import sys
import zipfile

source, target = sys.argv[1:]
with zipfile.ZipFile(source) as source_zip, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as target_zip:
    for entry in source_zip.infolist():
        contents = source_zip.read(entry.filename)
        if entry.filename == "lib/net10.0/TerraScale.DotNet.xml":
            contents += b"\n/api/v2/unpublished-package-route\n"
        target_zip.writestr(entry, contents)
PY
unpublished_sdk_digest="$(sha256sum "${unpublished_sdk}" | awk '{print $1}')"
expect_failure python3 -B "${verifier}" \
  --sdk-package "${unpublished_sdk}" --sdk-sha256 "${unpublished_sdk_digest}" \
  --ef-package "${ef_package}" --ef-sha256 "${ef_digest}" \
  --source-revision "${source_revision}"

duplicate_sdk="${test_root}/duplicate-sdk.nupkg"
python3 - "${sdk_package}" "${duplicate_sdk}" <<'PY'
import sys
import zipfile

source, target = sys.argv[1:]
with zipfile.ZipFile(source) as source_zip, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as target_zip:
    for entry in source_zip.infolist():
        target_zip.writestr(entry, source_zip.read(entry.filename))
    target_zip.writestr("TerraScale.DotNet.nuspec", b"duplicate")
PY
duplicate_sdk_digest="$(sha256sum "${duplicate_sdk}" | awk '{print $1}')"
expect_failure python3 -B "${verifier}" \
  --sdk-package "${duplicate_sdk}" --sdk-sha256 "${duplicate_sdk_digest}" \
  --ef-package "${ef_package}" --ef-sha256 "${ef_digest}" \
  --source-revision "${source_revision}"

dirty_checkout="${test_root}/dirty-checkout"
git clone --no-local --quiet "${repository_root}" "${dirty_checkout}"
git -C "${dirty_checkout}" config user.email test@example.invalid
git -C "${dirty_checkout}" config user.name "Package-set verifier test"
mkdir -p "${dirty_checkout}/tests/TerraScale.PackageSetConsumer"
cp "${verifier}" "${dirty_checkout}/scripts/verify-package-set-candidate.py"
cp "${repository_root}/tests/TerraScale.PackageSetConsumer/Program.cs" \
  "${dirty_checkout}/tests/TerraScale.PackageSetConsumer/Program.cs"
cp "${repository_root}/tests/TerraScale.PackageSetConsumer/TerraScale.PackageSetConsumer.csproj" \
  "${dirty_checkout}/tests/TerraScale.PackageSetConsumer/TerraScale.PackageSetConsumer.csproj"
cp "${repository_root}/src/TerraScale.EntityFrameworkCore/TerraScale.EntityFrameworkCore.csproj" \
  "${dirty_checkout}/src/TerraScale.EntityFrameworkCore/TerraScale.EntityFrameworkCore.csproj"
git -C "${dirty_checkout}" add \
  scripts/verify-package-set-candidate.py \
  src/TerraScale.EntityFrameworkCore/TerraScale.EntityFrameworkCore.csproj \
  tests/TerraScale.PackageSetConsumer/Program.cs \
  tests/TerraScale.PackageSetConsumer/TerraScale.PackageSetConsumer.csproj
if ! git -C "${dirty_checkout}" diff --cached --quiet; then
  git -C "${dirty_checkout}" commit --quiet -m "test package-set verifier baseline"
fi

if [[ -z "${handoff_path:-}" ]]; then
  handoff_package_root="${test_root}/handoff-candidate"
  mkdir -p "${handoff_package_root}"
  handoff_revision="$(git -C "${dirty_checkout}" rev-parse HEAD)"
  handoff_tree="$(git -C "${dirty_checkout}" rev-parse HEAD^{tree})"
  git -C "${dirty_checkout}" update-ref refs/remotes/origin/develop "${handoff_revision}"
  dotnet pack "${dirty_checkout}/src/TerraScale.DotNet/TerraScale.DotNet.csproj" \
    --configuration Release \
    --property:RepositoryCommit="${handoff_revision}" \
    --output "${handoff_package_root}" >/dev/null
  dotnet pack "${dirty_checkout}/src/TerraScale.EntityFrameworkCore/TerraScale.EntityFrameworkCore.csproj" \
    --configuration Release \
    --property:RepositoryCommit="${handoff_revision}" \
    --output "${handoff_package_root}" >/dev/null
  handoff_sdk="${handoff_package_root}/TerraScale.DotNet.0.1.0.nupkg"
  handoff_ef="${handoff_package_root}/TerraScale.EntityFrameworkCore.0.1.0.nupkg"
  isolated_handoff="${handoff_package_root}/terrascale-dotnet-package-set-handoff.json"
  python3 - \
    "${isolated_handoff}" \
    "$(sha256sum "${handoff_sdk}" | awk '{print $1}')" \
    "$(sha256sum "${handoff_ef}" | awk '{print $1}')" \
    "${handoff_revision}" \
    "${handoff_tree}" <<'PY'
import json
import sys

path, sdk_digest, ef_digest, revision, tree = sys.argv[1:]
handoff = {
    "format": "TerraScale.DotNet.PackageSetHandoff/v2",
    "packages": [
        {
            "id": "TerraScale.DotNet",
            "path": "TerraScale.DotNet.0.1.0.nupkg",
            "sha256": sdk_digest,
            "version": "0.1.0",
        },
        {
            "id": "TerraScale.EntityFrameworkCore",
            "path": "TerraScale.EntityFrameworkCore.0.1.0.nupkg",
            "sha256": ef_digest,
            "version": "0.1.0",
        },
    ],
    "source": {"ref": "refs/heads/develop", "revision": revision, "tree": tree},
}
with open(path, "x", encoding="utf-8") as stream:
    json.dump(handoff, stream, sort_keys=True, separators=(",", ":"))
    stream.write("\n")
PY
  TERRASCALE_PACKAGE_RECEIPT_PATH="${test_root}/isolated-package-set-receipt.json" \
    python3 -B "${dirty_checkout}/scripts/verify-package-set-candidate.py" \
    --handoff "${isolated_handoff}" >/dev/null
  linked_handoff="${test_root}/linked-package-set-handoff.json"
  ln -s "${isolated_handoff}" "${linked_handoff}"
  expect_failure python3 -B "${dirty_checkout}/scripts/verify-package-set-candidate.py" \
    --handoff "${linked_handoff}"

  duplicate_key_handoff="${handoff_package_root}/duplicate-key-handoff.json"
  python3 - "${isolated_handoff}" "${duplicate_key_handoff}" <<'PY'
import sys

source, target = sys.argv[1:]
with open(source, encoding="utf-8") as stream:
    contents = stream.read()
marker = '{"format":'
if not contents.startswith(marker):
    raise SystemExit("canonical handoff fixture did not start with the format field")
contents = contents.replace(
    marker,
    '{"format":"TerraScale.DotNet.PackageSetHandoff/v1","format":',
    1,
)
with open(target, "x", encoding="utf-8") as stream:
    stream.write(contents)
PY
  expect_failure python3 -B "${dirty_checkout}/scripts/verify-package-set-candidate.py" \
    --handoff "${duplicate_key_handoff}"
fi

printf '\n# dirty package-set source fixture\n' >> "${dirty_checkout}/README.md"
expect_failure python3 -B "${dirty_checkout}/scripts/verify-package-set-candidate.py"

if [[ -n "${handoff_path:-}" ]]; then
  malformed_handoff="${test_root}/malformed-handoff.json"
  python3 - "${handoff_path}" "${malformed_handoff}" <<'PY'
import json
import sys

source, target = sys.argv[1:]
with open(source, encoding="utf-8") as stream:
    handoff = json.load(stream)
handoff["packages"] = handoff["packages"][:1]
with open(target, "x", encoding="utf-8") as stream:
    json.dump(handoff, stream)
PY
  expect_failure python3 -B "${verifier}" --handoff "${malformed_handoff}"
fi

echo "package-set verifier regression fixtures passed"
