#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
verification_root="$(mktemp -d)"
trap 'rm -rf "${verification_root}"' EXIT

package_output="${verification_root}/packed"
package_source="${verification_root}/source"
package_cache="${verification_root}/packages"
build_output="${verification_root}/build"
consumer_root="${verification_root}/consumer"
mkdir -p "${package_output}" "${package_source}" "${package_cache}" "${build_output}" "${consumer_root}"

package_id="TerraScale.DotNet"
package_version="0.1.0"
package_name="${package_id}.${package_version}.nupkg"
handoff_mode=false
handoff_ref=""
handoff_tree=""
receipt_path="${TERRASCALE_PACKAGE_RECEIPT_PATH:-}"

if ! command -v python3 > /dev/null 2>&1; then
  echo "python3 is required for strict handoff, nuspec, ZIP, and assets validation" >&2
  exit 1
fi

if ! source_revision="$(git -C "${repository_root}" rev-parse --verify HEAD 2>/dev/null)"; then
  echo "the package source revision could not be resolved from Git" >&2
  exit 1
fi

expected_digest=""
if [[ "$#" -eq 0 ]]; then
  if ! source_status="$(git -C "${repository_root}" status --porcelain=v1 --untracked-files=all 2>/dev/null)"; then
    echo "the package source status could not be resolved from Git" >&2
    exit 1
  fi

  if [[ -n "${source_status}" ]]; then
    echo "refusing no-argument pack from a dirty source tree; supply a verified immutable package" >&2
    exit 1
  fi

  dotnet pack "${repository_root}/src/TerraScale.DotNet/TerraScale.DotNet.csproj" \
    --configuration Release \
    --property:RepositoryCommit="${source_revision}" \
    --output "${package_output}"
  package_path="${package_output}/${package_name}"
elif [[ "$#" -eq 2 && "$1" == "--package" ]]; then
  package_path="$2"
elif [[ "$#" -eq 4 && "$1" == "--package" && "$3" == "--sha256" ]]; then
  package_path="$2"
  expected_digest="${4,,}"
elif [[ "$#" -eq 6 && "$1" == "--package" && "$3" == "--sha256" && "$5" == "--source-revision" ]]; then
  package_path="$2"
  expected_digest="${4,,}"
  source_revision="${6,,}"
elif [[ "$#" -eq 2 && "$1" == "--handoff" ]]; then
  handoff_mode=true
  handoff_path="$2"
else
  echo "usage: $0 [--package path/to/TerraScale.DotNet.0.1.0.nupkg [--sha256 digest] [--source-revision commit]] | [--handoff path/to/handoff.json]" >&2
  exit 2
fi

if [[ "${handoff_mode}" == true ]]; then
  handoff_fields_file="${verification_root}/handoff-fields"
  if ! python3 - "${handoff_path}" > "${handoff_fields_file}" <<'PY'
import json
import os
import re
import stat
import sys

path = sys.argv[1]
try:
    path_stat = os.lstat(path)
    if not stat.S_ISREG(path_stat.st_mode) or stat.S_ISLNK(path_stat.st_mode):
        raise OSError("handoff must be a non-symlink regular file")
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_CLOEXEC", 0)
    descriptor = os.open(path, flags)
    try:
        opened_stat = os.fstat(descriptor)
        if (path_stat.st_dev, path_stat.st_ino) != (opened_stat.st_dev, opened_stat.st_ino):
            raise OSError("handoff changed while it was opened")
        with os.fdopen(descriptor, encoding="utf-8") as stream:
            descriptor = -1
            handoff = json.load(stream)
    finally:
        if descriptor >= 0:
            os.close(descriptor)
except (OSError, json.JSONDecodeError) as error:
    raise SystemExit(f"package handoff is not valid JSON: {error}")

if not isinstance(handoff, dict) or set(handoff) != {"format", "package", "source"} or handoff["format"] != "TerraScale.DotNet.PackageHandoff/v1":
    raise SystemExit("package handoff format is not TerraScale.DotNet.PackageHandoff/v1")
package = handoff["package"]
source = handoff["source"]
if not isinstance(package, dict) or not isinstance(source, dict) or set(package) != {"path", "sha256"} or set(source) != {"revision", "ref", "tree"}:
    raise SystemExit("package handoff contains unexpected fields")
values = [package["path"], package["sha256"], source["revision"], source["ref"], source["tree"]]
if not all(isinstance(value, str) and value and "\n" not in value and "\r" not in value for value in values):
    raise SystemExit("package handoff fields must be non-empty strings without line breaks")
if not os.path.isabs(package["path"]):
    raise SystemExit("package handoff path must be absolute")
if not re.fullmatch(r"[0-9a-f]{64}", package["sha256"]):
    raise SystemExit("package handoff digest must be lowercase SHA-256")
if not re.fullmatch(r"[0-9a-f]{40}", source["revision"]):
    raise SystemExit("package handoff revision must be a 40-character lowercase hexadecimal Git commit")
if not re.fullmatch(r"[0-9a-f]{40}", source["tree"]):
    raise SystemExit("package handoff tree must be a 40-character lowercase Git tree")
print(package["path"])
print(package["sha256"])
print(source["revision"])
print(source["ref"])
print(source["tree"])
PY
  then
    echo "package handoff could not be parsed" >&2
    exit 2
  fi
  mapfile -t handoff_fields < "${handoff_fields_file}"
  if [[ "${#handoff_fields[@]}" -ne 5 ]]; then
    echo "package handoff did not contain the required fields" >&2
    exit 2
  fi
  package_path="${handoff_fields[0]}"
  expected_digest="${handoff_fields[1]}"
  source_revision="${handoff_fields[2]}"
  handoff_ref="${handoff_fields[3]}"
  handoff_tree="${handoff_fields[4]}"
fi

if [[ ! "${source_revision}" =~ ^[0-9a-f]{40}$ ]]; then
  echo "source revision must be a 40-character lowercase hexadecimal Git commit" >&2
  exit 2
fi

if ! checked_out_revision="$(git -C "${repository_root}" rev-parse --verify HEAD 2>/dev/null)" ||
   ! checked_out_tree="$(git -C "${repository_root}" rev-parse --verify HEAD^{tree} 2>/dev/null)"; then
  echo "the checked-out Git source identity could not be resolved" >&2
  exit 1
fi

if ! git -C "${repository_root}" cat-file -e "${source_revision}^{commit}" 2>/dev/null; then
  echo "source revision ${source_revision} is not a commit in the expected repository" >&2
  exit 2
fi

if [[ "${handoff_mode}" == true ]]; then
  if [[ "${handoff_ref}" != "refs/heads/develop" ]]; then
    echo "package handoff source ref must be refs/heads/develop" >&2
    exit 2
  fi
  if [[ "${source_revision}" != "${checked_out_revision}" || "${handoff_tree}" != "${checked_out_tree}" ]]; then
    echo "package handoff source identity does not match the checked-out protected source" >&2
    exit 1
  fi
  if ! resolved_ref="$(git -C "${repository_root}" rev-parse --verify "refs/remotes/origin/develop^{commit}" 2>/dev/null)"; then
    if ! resolved_ref="$(git -C "${repository_root}" rev-parse --verify "${handoff_ref}^{commit}" 2>/dev/null)"; then
      echo "package handoff source ref does not resolve in the checked-out repository" >&2
      exit 1
    fi
  fi
  if [[ "${resolved_ref}" != "${source_revision}" ]]; then
    echo "package handoff source ref does not resolve to the checked-out source revision" >&2
    exit 1
  fi
fi

staged_package="${package_source}/${package_name}"
candidate_identity_file="${verification_root}/candidate-identity"
if ! python3 - "${package_path}" "${staged_package}" > "${candidate_identity_file}" <<'PY'
import hashlib
import os
import stat
import sys

source_path, target_path = sys.argv[1:]
try:
    path_stat = os.lstat(source_path)
    if not stat.S_ISREG(path_stat.st_mode) or stat.S_ISLNK(path_stat.st_mode):
        raise OSError("candidate package must be a non-symlink regular file")
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_CLOEXEC", 0)
    descriptor = os.open(source_path, flags)
    try:
        opened_stat = os.fstat(descriptor)
        identity = (path_stat.st_dev, path_stat.st_ino, path_stat.st_size, path_stat.st_mtime_ns, path_stat.st_ctime_ns)
        opened_identity = (opened_stat.st_dev, opened_stat.st_ino, opened_stat.st_size, opened_stat.st_mtime_ns, opened_stat.st_ctime_ns)
        if identity != opened_identity:
            raise OSError("candidate package changed while it was opened")
        digest = hashlib.sha256()
        source_stream = os.fdopen(descriptor, "rb")
        descriptor = -1
        try:
            with open(target_path, "xb") as target_stream:
                while True:
                    chunk = source_stream.read(1024 * 1024)
                    if not chunk:
                        break
                    digest.update(chunk)
                    target_stream.write(chunk)
            final_stat = os.fstat(source_stream.fileno())
        finally:
            source_stream.close()
        path_after = os.lstat(source_path)
        final_identity = (final_stat.st_dev, final_stat.st_ino, final_stat.st_size, final_stat.st_mtime_ns, final_stat.st_ctime_ns)
        path_after_identity = (path_after.st_dev, path_after.st_ino, path_after.st_size, path_after.st_mtime_ns, path_after.st_ctime_ns)
        if final_identity != opened_identity or path_after_identity != opened_identity:
            raise OSError("candidate package changed while it was copied")
    finally:
        if descriptor >= 0:
            os.close(descriptor)
except OSError as error:
    raise SystemExit(f"candidate package could not be copied safely: {error}")

print(digest.hexdigest())
print("%d:%d:%d:%d:%d" % identity)
PY
then
  echo "candidate package failed no-follow identity validation" >&2
  exit 2
fi
mapfile -t candidate_fields < "${candidate_identity_file}"
if [[ "${#candidate_fields[@]}" -ne 2 ]]; then
  echo "candidate package identity could not be recorded" >&2
  exit 1
fi
package_digest="${candidate_fields[0]}"
candidate_identity="${candidate_fields[1]}"
if [[ -n "${expected_digest}" ]]; then
  if [[ ! "${expected_digest}" =~ ^[[:xdigit:]]{64}$ ]]; then
    echo "--sha256 must be a 64-character hexadecimal digest" >&2
    exit 2
  fi

  if [[ "${package_digest}" != "${expected_digest}" ]]; then
    echo "candidate package digest does not match --sha256" >&2
    exit 1
  fi
fi

mapfile -t staged_entries < <(find "${package_source}" -mindepth 1 -maxdepth 1 -type f -print)
if [[ "${#staged_entries[@]}" -ne 1 || "${staged_entries[0]}" != "${staged_package}" ]]; then
  echo "isolated package source contains an unexpected package" >&2
  exit 1
fi

if ! python3 - "${staged_package}" <<'PY'
import re
import stat
import sys
import zipfile

package_path = sys.argv[1]
seen = set()
try:
    with zipfile.ZipFile(package_path) as archive:
        for entry in archive.infolist():
            name = entry.filename
            if not name or "\x00" in name or "\\" in name or name.startswith("/") or re.match(r"^[A-Za-z]:", name):
                raise SystemExit(f"candidate package contains an unsafe ZIP path: {name!r}")
            parts = name.split("/")
            if ".." in parts or "*" in name or "?" in name or (("[" in name or "]" in name) and name != "[Content_Types].xml"):
                raise SystemExit(f"candidate package contains an unsafe ZIP path: {name!r}")
            if name in seen:
                raise SystemExit(f"candidate package contains a duplicate ZIP path: {name!r}")
            seen.add(name)

            mode = (entry.external_attr >> 16) & 0xffff
            file_type = stat.S_IFMT(mode)
            is_directory = name.endswith("/")
            if file_type and file_type not in (stat.S_IFREG, stat.S_IFDIR):
                raise SystemExit(f"candidate package contains a non-regular ZIP entry: {name!r}")
            if is_directory and file_type not in (0, stat.S_IFDIR):
                raise SystemExit(f"candidate package contains an invalid ZIP directory entry: {name!r}")
            if not is_directory and file_type == stat.S_IFDIR:
                raise SystemExit(f"candidate package contains a directory with a file path: {name!r}")
        if archive.testzip() is not None:
            raise SystemExit("candidate package contains a ZIP entry with an invalid CRC")
except (OSError, zipfile.BadZipFile, zipfile.LargeZipFile) as error:
    raise SystemExit(f"candidate package archive is not structurally valid: {error}")
PY
then
  echo "candidate package failed strict ZIP structure validation" >&2
  exit 1
fi

package_listing_file="${verification_root}/package-listing.txt"
if ! unzip -Z1 "${staged_package}" > "${package_listing_file}" 2>/dev/null; then
  echo "candidate package archive could not be listed" >&2
  exit 1
fi
if [[ ! -s "${package_listing_file}" ]]; then
  echo "candidate package archive is empty" >&2
  exit 1
fi
if grep -aEi '\.(pdb|user|suo)$|(^|/)(bin|obj)/' "${package_listing_file}" > /dev/null; then
  echo "candidate package contains debug or build-only files" >&2
  exit 1
else
  scan_status=$?
  if [[ "${scan_status}" -gt 1 ]]; then
    echo "candidate package archive listing could not be scanned" >&2
    exit 1
  fi
fi

nuspec_path="${verification_root}/${package_id}.nuspec"
if ! unzip -p "${staged_package}" "${package_id}.nuspec" > "${nuspec_path}" 2>/dev/null; then
  echo "candidate package nuspec could not be extracted" >&2
  exit 1
fi

if ! command -v python3 > /dev/null 2>&1; then
  echo "python3 is required for strict nuspec and assets XML/JSON validation" >&2
  exit 1
fi
if ! python3 - "${nuspec_path}" "${source_revision}" "${package_id}" "${package_version}" <<'PY'
import sys
import xml.etree.ElementTree as ET

nuspec_path, expected_revision, expected_id, expected_version = sys.argv[1:]
namespace = "{http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd}"

try:
    root = ET.parse(nuspec_path).getroot()
except (ET.ParseError, OSError) as error:
    raise SystemExit(f"candidate package nuspec is not valid XML: {error}")

if root.tag != f"{namespace}package":
    raise SystemExit("candidate package nuspec has an unexpected XML root")
metadata = root.find(f"{namespace}metadata")
if metadata is None:
    raise SystemExit("candidate package nuspec has no metadata element")

def exactly_one(name):
    values = metadata.findall(f"{namespace}{name}")
    if len(values) != 1:
        raise SystemExit(f"candidate package nuspec must contain exactly one {name} element")
    return values[0]

if exactly_one("id").text != expected_id or exactly_one("version").text != expected_version:
    raise SystemExit("candidate package nuspec identity does not match TerraScale.DotNet 0.1.0")

license_element = exactly_one("license")
if license_element.attrib != {"type": "expression"} or license_element.text != "MIT":
    raise SystemExit("candidate package nuspec license metadata is invalid")

repository = exactly_one("repository")
if repository.attrib.get("type") != "git" or \
        repository.attrib.get("url") != "https://git.terrascale.tech/TerraScale/TerraScale.DotNet" or \
        repository.attrib.get("commit") != expected_revision:
    raise SystemExit("candidate package nuspec repository metadata does not identify the exact source")

release_notes = exactly_one("releaseNotes").text or ""
for marker in ("TerraScale public API /api/v1", "stable .NET 10 LTS"):
    if marker not in release_notes:
        raise SystemExit(f"candidate package nuspec release notes are missing: {marker}")

dependencies = exactly_one("dependencies")
groups = dependencies.findall(f"{namespace}group")
if not any(group.attrib == {"targetFramework": "net10.0"} for group in groups):
    raise SystemExit("candidate package nuspec has no net10.0 dependency group")
PY
then
  echo "candidate package metadata failed strict XML validation" >&2
  exit 1
fi

entry_index=0
while IFS= read -r package_entry; do
  entry_index=$((entry_index + 1))
  entry_file="${verification_root}/entry-${entry_index}"
  package_entry_pattern="${package_entry//[/[[]}"
  if ! unzip -p "${staged_package}" "${package_entry_pattern}" > "${entry_file}" 2>/dev/null; then
    echo "candidate package entry could not be extracted: ${package_entry}" >&2
    exit 1
  fi

  if grep -aEi 'tsauth_[A-Za-z0-9_.-]+|localhost([:/]|$)|127\.0\.0\.1|(^|[^[:alnum:]])/(home|workspaces)/|\\Users\\|/api/v1/internal' "${entry_file}" > /dev/null; then
    echo "candidate package contents contain a secret, local path, or unpublished endpoint: ${package_entry}" >&2
    exit 1
  else
    scan_status=$?
    if [[ "${scan_status}" -gt 1 ]]; then
      echo "candidate package entry could not be scanned: ${package_entry}" >&2
      exit 1
    fi
  fi
done < "${package_listing_file}"

if ! cp "${repository_root}/tests/TerraScale.PackageConsumer/Program.cs" "${consumer_root}/Program.cs" ||
   ! cp "${repository_root}/tests/TerraScale.PackageConsumer/TerraScale.PackageConsumer.csproj" "${consumer_root}/TerraScale.PackageConsumer.csproj"; then
  echo "clean package consumer could not be staged outside the repository" >&2
  exit 1
fi

if find "${consumer_root}" -maxdepth 1 -name 'Directory.Build.*' -print -quit | grep -aE '.' > /dev/null; then
  echo "clean package consumer unexpectedly inherited repository build imports" >&2
  exit 1
fi

dotnet restore "${consumer_root}/TerraScale.PackageConsumer.csproj" \
  --packages "${package_cache}" \
  --artifacts-path "${build_output}" \
  --source "${package_source}" \
  --source "https://api.nuget.org/v3/index.json" \
  --no-cache \
  --force-evaluate

assets_path="$(find "${build_output}" -type f -name project.assets.json -print -quit)"
if [[ -z "${assets_path}" ]]; then
  echo "clean consumer restore did not produce project assets" >&2
  exit 1
fi
if ! python3 - "${assets_path}" "${package_id}" "${package_version}" <<'PY'
import json
import sys

assets_path, expected_id, expected_version = sys.argv[1:]
try:
    with open(assets_path, encoding="utf-8") as stream:
        assets = json.load(stream)
except (OSError, json.JSONDecodeError) as error:
    raise SystemExit(f"consumer assets are not valid JSON: {error}")

identity = f"{expected_id}/{expected_version}"
if not any(identity in target for target in assets.get("targets", {}).values()):
    raise SystemExit(f"consumer assets do not reference {identity}")
PY
then
  echo "clean consumer did not restore the expected package identity" >&2
  exit 1
fi

mapfile -t restored_packages < <(find "${package_cache}/terrascale.dotnet/${package_version}" -mindepth 1 -maxdepth 1 -type f -iname '*.nupkg' -print 2>/dev/null)
if [[ "${#restored_packages[@]}" -ne 1 ]]; then
  echo "NuGet did not restore the expected TerraScale.DotNet package bytes" >&2
  exit 1
fi
restored_package="${restored_packages[0]}"
restored_digest="$(sha256sum "${restored_package}" | awk '{print $1}')"
if [[ "${package_digest}" != "${restored_digest}" ]]; then
  echo "restored package bytes do not match the supplied candidate" >&2
  exit 1
fi

dotnet build "${consumer_root}/TerraScale.PackageConsumer.csproj" \
  --configuration Release \
  --no-restore \
  --artifacts-path "${build_output}" \
  --property:TreatWarningsAsErrors=true

if ! python3 - "${package_path}" "${candidate_identity}" <<'PY'
import os
import stat
import sys

path, expected = sys.argv[1:]
try:
    path_stat = os.lstat(path)
    if not stat.S_ISREG(path_stat.st_mode) or stat.S_ISLNK(path_stat.st_mode):
        raise OSError("candidate package is no longer a non-symlink regular file")
    actual = "%d:%d:%d:%d:%d" % (path_stat.st_dev, path_stat.st_ino, path_stat.st_size, path_stat.st_mtime_ns, path_stat.st_ctime_ns)
    if actual != expected:
        raise OSError("candidate package identity changed during verification")
except OSError as error:
    raise SystemExit(str(error))
PY
then
  echo "candidate package changed during exact-byte verification" >&2
  exit 1
fi

if [[ -n "${receipt_path}" ]]; then
  if ! python3 - "${receipt_path}" "${package_path}" "${package_name}" "${package_digest}" "${source_revision}" "${handoff_ref}" "${checked_out_tree}" <<'PY'
import json
import os
import stat
import sys

path, package_path, package_name, package_digest, source_revision, source_ref, source_tree = sys.argv[1:]
parent = os.path.dirname(path) or "."
os.makedirs(parent, exist_ok=True)
if os.path.lexists(path) and stat.S_ISLNK(os.lstat(path).st_mode):
    raise SystemExit("receipt path must not be a symlink")
temporary = f"{path}.{os.getpid()}.tmp"
receipt = {
    "format": "TerraScale.DotNet.PackageVerificationReceipt/v1",
    "verified": True,
    "package": {"name": package_name, "path": package_path, "sha256": package_digest},
    "source": {"revision": source_revision, "ref": source_ref, "tree": source_tree},
    "consumer": {"targetFramework": "net10.0", "warningsAsErrors": True},
}
try:
    with open(temporary, "x", encoding="utf-8") as stream:
        json.dump(receipt, stream, sort_keys=True, separators=(",", ":"))
        stream.write("\n")
    os.replace(temporary, path)
except OSError as error:
    try:
        os.unlink(temporary)
    except OSError:
        pass
    raise SystemExit(f"verification receipt could not be written: {error}")
PY
  then
    echo "verification receipt could not be written" >&2
    exit 1
  fi
fi

printf 'source-revision:%s\nsha256:%s\npackage:%s\n' "${source_revision}" "${package_digest}" "${package_name}"
