#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
verifier="${repository_root}/scripts/verify-package-candidate.sh"
test_root="$(mktemp -d)"
trap 'rm -rf "${test_root}"' EXIT

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

source_revision="$(git -C "${repository_root}" rev-parse HEAD)"
parent_revision="$(git -C "${repository_root}" rev-parse HEAD^)"

handoff_path=""
if [[ "$#" -eq 2 && "$1" == "--handoff" ]]; then
  handoff_path="$2"
elif [[ "$#" -ne 0 ]]; then
  echo "usage: $0 [--handoff path/to/handoff.json]" >&2
  exit 2
fi

unset TERRASCALE_PACKAGE_RECEIPT_PATH

# A no-argument pack must reject dirty source, even when the source is a valid
# isolated checkout. The real checkout is intentionally dirty during fleet work.
dirty_checkout="${test_root}/dirty-checkout"
git clone --no-local --quiet "${repository_root}" "${dirty_checkout}"
git -C "${dirty_checkout}" config user.email test@example.invalid
git -C "${dirty_checkout}" config user.name "Package verifier test"
cp "${verifier}" "${dirty_checkout}/scripts/verify-package-candidate.sh"
git -C "${dirty_checkout}" add scripts/verify-package-candidate.sh
git -C "${dirty_checkout}" commit --quiet -m "test verifier baseline"
printf '\n# dirty source fixture\n' >> "${dirty_checkout}/README.md"
expect_failure bash "${dirty_checkout}/scripts/verify-package-candidate.sh"

if [[ -n "${handoff_path}" ]]; then
  mapfile -t handoff_values < <(python3 - "${handoff_path}" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    handoff = json.load(stream)
print(handoff["package"]["path"])
print(handoff["sha256"])
print(handoff["source"]["revision"])
PY
  )
  if [[ "${#handoff_values[@]}" -ne 3 ]]; then
    echo "handoff fixture did not contain the expected candidate fields" >&2
    exit 1
  fi
  package_path="${handoff_values[0]}"
  package_digest="${handoff_values[1]}"
  source_revision="${handoff_values[2]}"
else
  package_root="${test_root}/package"
  mkdir -p "${package_root}"
  dotnet pack "${repository_root}/src/TerraScale.DotNet/TerraScale.DotNet.csproj" \
    --configuration Release \
    --property:RepositoryCommit="${source_revision}" \
    --output "${package_root}" \
    --no-restore >/dev/null
  package_path="${package_root}/TerraScale.DotNet.0.1.0.nupkg"
  package_digest="$(sha256sum "${package_path}" | awk '{print $1}')"
fi

# The supplied-package path is the only path allowed from a dirty checkout.
if [[ -n "${handoff_path}" ]]; then
  "${verifier}" --handoff "${handoff_path}" >/dev/null
else
  "${verifier}" --package "${package_path}" --sha256 "${package_digest}" --source-revision "${source_revision}" >/dev/null
fi
expect_failure "${verifier}" --package "${package_path}" --sha256 "$(printf '0%.0s' {1..64})" --source-revision "${source_revision}"
expect_failure "${verifier}" --package "${package_path}" --sha256 "${package_digest}" --source-revision "$(printf '0%.0s' {1..40})"
expect_failure "${verifier}" --package "${package_path}" --sha256 "${package_digest}" --source-revision "${parent_revision}"

linked_package="${test_root}/linked-package.nupkg"
ln -s "${package_path}" "${linked_package}"
expect_failure "${verifier}" --package "${linked_package}" --sha256 "${package_digest}" --source-revision "${source_revision}"

# A sibling package must not be eligible as a restore source.
sibling_source="${test_root}/sibling-source"
mkdir -p "${sibling_source}"
cp "${package_path}" "${sibling_source}/TerraScale.DotNet.0.1.0.nupkg"
cp "${package_path}" "${sibling_source}/TerraScale.DotNet.0.1.0-sibling.nupkg"
"${verifier}" --package "${sibling_source}/TerraScale.DotNet.0.1.0.nupkg" \
  --sha256 "${package_digest}" --source-revision "${source_revision}" >/dev/null

# XML comments containing a forged repository record must not satisfy metadata validation.
spoofed_package="${test_root}/spoofed.nupkg"
python3 - "${package_path}" "${spoofed_package}" "${source_revision}" "${parent_revision}" <<'PY'
import sys
import zipfile

source, target, expected, forged = sys.argv[1:]
with zipfile.ZipFile(source) as source_zip, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as target_zip:
    for entry in source_zip.infolist():
        contents = source_zip.read(entry.filename)
        if entry.filename == "TerraScale.DotNet.nuspec":
            text = contents.decode("utf-8")
            text = text.replace(
                f'commit="{expected}"',
                f'<!-- <repository commit="{expected}" /> --><repository commit="{forged}"',
                1)
            contents = text.encode("utf-8")
        target_zip.writestr(entry, contents)
PY
spoofed_digest="$(sha256sum "${spoofed_package}" | awk '{print $1}')"
expect_failure "${verifier}" --package "${spoofed_package}" --sha256 "${spoofed_digest}" --source-revision "${source_revision}"

# Archive corruption and forbidden content must both fail closed without a grep
# short-read/SIGPIPE escape.
corrupt_package="${test_root}/corrupt.nupkg"
head -c 128 "${package_path}" > "${corrupt_package}"
corrupt_digest="$(sha256sum "${corrupt_package}" | awk '{print $1}')"
expect_failure "${verifier}" --package "${corrupt_package}" --sha256 "${corrupt_digest}" --source-revision "${source_revision}"

for fixture_type in symlink fifo traversal wildcard absolute duplicate device; do
  structural_package="${test_root}/${fixture_type}.nupkg"
  python3 - "${package_path}" "${structural_package}" "${fixture_type}" <<'PY'
import stat
import sys
import zipfile

source, target, fixture_type = sys.argv[1:]
with zipfile.ZipFile(source) as source_zip, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as target_zip:
    for entry in source_zip.infolist():
        target_zip.writestr(entry, source_zip.read(entry.filename))
    if fixture_type == "symlink":
        entry = zipfile.ZipInfo("unsafe-link")
        entry.create_system = 3
        entry.external_attr = (stat.S_IFLNK | 0o777) << 16
        target_zip.writestr(entry, b"target")
    elif fixture_type == "fifo":
        entry = zipfile.ZipInfo("unsafe-fifo")
        entry.create_system = 3
        entry.external_attr = (stat.S_IFIFO | 0o644) << 16
        target_zip.writestr(entry, b"fifo")
    elif fixture_type == "traversal":
        entry = zipfile.ZipInfo("../unsafe-traversal")
        entry.create_system = 3
        entry.external_attr = (stat.S_IFREG | 0o644) << 16
        target_zip.writestr(entry, b"traversal")
    elif fixture_type == "wildcard":
        entry = zipfile.ZipInfo("unsafe*.txt")
        entry.create_system = 3
        entry.external_attr = (stat.S_IFREG | 0o644) << 16
        target_zip.writestr(entry, b"wildcard")
    elif fixture_type == "absolute":
        entry = zipfile.ZipInfo("/unsafe-absolute")
        entry.create_system = 3
        entry.external_attr = (stat.S_IFREG | 0o644) << 16
        target_zip.writestr(entry, b"absolute")
    elif fixture_type == "duplicate":
        entry = zipfile.ZipInfo("TerraScale.DotNet.nuspec")
        entry.create_system = 3
        entry.external_attr = (stat.S_IFREG | 0o644) << 16
        target_zip.writestr(entry, b"duplicate")
    else:
        entry = zipfile.ZipInfo("unsafe-device")
        entry.create_system = 3
        entry.external_attr = (stat.S_IFCHR | 0o600) << 16
        target_zip.writestr(entry, b"device")
PY
  structural_digest="$(sha256sum "${structural_package}" | awk '{print $1}')"
  expect_failure "${verifier}" --package "${structural_package}" --sha256 "${structural_digest}" --source-revision "${source_revision}"
done

secret_root="${test_root}/secret-package"
mkdir -p "${secret_root}"
unzip -q "${package_path}" -d "${secret_root}"
{ printf 'tsauth_forbidden_fixture\n'; head -c 1048576 /dev/zero; } > "${secret_root}/forbidden.txt"
secret_package="${test_root}/secret.nupkg"
(cd "${secret_root}" && zip -q -r "${secret_package}" .)
secret_digest="$(sha256sum "${secret_package}" | awk '{print $1}')"
expect_failure "${verifier}" --package "${secret_package}" --sha256 "${secret_digest}" --source-revision "${source_revision}"

echo "package verifier regression fixtures passed"
