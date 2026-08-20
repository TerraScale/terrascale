#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
verification_root="$(mktemp -d)"
trap 'rm -rf "${verification_root}"' EXIT

package_output="${verification_root}/candidate"
package_cache="${verification_root}/packages"
build_output="${verification_root}/build"
mkdir -p "${package_output}" "${package_cache}" "${build_output}"

if [[ "$#" -eq 0 ]]; then
  dotnet pack "${repository_root}/src/TerraScale.DotNet/TerraScale.DotNet.csproj" \
    --configuration Release \
    --output "${package_output}"
  package_path="${package_output}/TerraScale.DotNet.0.1.0.nupkg"
elif [[ "$#" -eq 2 && "$1" == "--package" ]]; then
  package_path="$(realpath "$2")"
else
  echo "usage: $0 [--package path/to/TerraScale.DotNet.0.1.0.nupkg]" >&2
  exit 2
fi

test -f "${package_path}"
package_source="$(dirname "${package_path}")"

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
  'TerraScale public API /api/v1'; do
  if ! grep -Fq "${marker}" <<<"${nuspec}"; then
    echo "candidate package metadata is missing: ${marker}" >&2
    exit 1
  fi
done

if grep -Eiq 'tsauth_[A-Za-z0-9_-]+|/home/|/workspaces/|\\Users\\|localhost|/api/v1/(admin|internal)' <<<"${nuspec}"; then
  echo "candidate package metadata contains a secret, local path, or unpublished endpoint" >&2
  exit 1
fi

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

sha256sum "${package_path}"
