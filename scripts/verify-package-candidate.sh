#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
verification_root="$(mktemp -d)"
trap 'rm -rf "${verification_root}"' EXIT

package_output="${verification_root}/candidate"
package_cache="${verification_root}/packages"
build_output="${verification_root}/build"
mkdir -p "${package_output}" "${package_cache}" "${build_output}"

expected_digest=""
if [[ "$#" -eq 0 ]]; then
  dotnet pack "${repository_root}/src/TerraScale.DotNet/TerraScale.DotNet.csproj" \
    --configuration Release \
    --output "${package_output}"
  package_path="${package_output}/TerraScale.DotNet.0.1.0.nupkg"
elif [[ "$#" -eq 2 && "$1" == "--package" ]]; then
  package_path="$(realpath "$2")"
elif [[ "$#" -eq 4 && "$1" == "--package" && "$3" == "--sha256" ]]; then
  package_path="$(realpath "$2")"
  expected_digest="${4,,}"
else
  echo "usage: $0 [--package path/to/TerraScale.DotNet.0.1.0.nupkg [--sha256 digest]]" >&2
  exit 2
fi

test -f "${package_path}"
package_source="$(dirname "${package_path}")"
package_digest="$(sha256sum "${package_path}" | awk '{print $1}')"
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

package_listing="$(unzip -Z1 "${package_path}")"
if grep -Eiq '\.(pdb|user|suo)$|(^|/)(bin|obj)/' <<<"${package_listing}"; then
  echo "candidate package contains debug or build-only files" >&2
  exit 1
fi

nuspec="$(unzip -p "${package_path}" TerraScale.DotNet.nuspec)"
for marker in \
  '<license type="expression">MIT</license>' \
  '<repository type="git" url="https://git.terrascale.tech/TerraScale/TerraScale.DotNet"' \
  '<group targetFramework="net10.0">' \
  'TerraScale public API /api/v1' \
  'stable .NET 10 LTS' \
  'Public Alpha API candidate qualification'; do
  if ! grep -Fq "${marker}" <<<"${nuspec}"; then
    echo "candidate package metadata is missing: ${marker}" >&2
    exit 1
  fi
done

while IFS= read -r package_entry; do
  package_entry_pattern="${package_entry//[/[[]}"
  if unzip -p "${package_path}" "${package_entry_pattern}" |
    grep -aEiq 'tsauth_[A-Za-z0-9_.-]+|localhost([:/]|$)|127\.0\.0\.1|(^|[^[:alnum:]])/(home|workspaces)/|\\Users\\|/api/v1/internal'; then
    echo "candidate package contents contain a secret, local path, or unpublished endpoint: ${package_entry}" >&2
    exit 1
  fi
done <<<"${package_listing}"

dotnet restore "${repository_root}/tests/TerraScale.PackageConsumer/TerraScale.PackageConsumer.csproj" \
  --packages "${package_cache}" \
  --artifacts-path "${build_output}" \
  --source "${package_source}" \
  --source "https://api.nuget.org/v3/index.json" \
  --property:RestoreNoCache=true

dotnet build "${repository_root}/tests/TerraScale.PackageConsumer/TerraScale.PackageConsumer.csproj" \
  --configuration Release \
  --no-restore \
  --artifacts-path "${build_output}"

post_build_digest="$(sha256sum "${package_path}" | awk '{print $1}')"
if [[ "${package_digest}" != "${post_build_digest}" ]]; then
  echo "candidate package changed during exact-byte verification" >&2
  exit 1
fi

printf 'sha256:%s  %s\n' "${post_build_digest}" "${package_path}"
