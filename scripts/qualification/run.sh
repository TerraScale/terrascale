#!/usr/bin/env bash
set -euo pipefail
regression=false
if [[ "${1:-}" == "--regression" ]]; then
  regression=true
  phase=regression
elif [[ "${1:-}" == "--phase" ]]; then
  phase="${2:-}"
else
  phase="${1:-}"
fi
if [[ "$phase" != seed && "$phase" != verify && "$phase" != exercise && "$phase" != cleanup && "$phase" != regression ]]; then
  echo 'usage: run.sh --phase seed|verify|exercise|cleanup | --regression' >&2; exit 2
fi
root="$(cd "$(dirname "$0")/../.." && pwd)"
required=(TERRASCALE_SDK_NUPKG TERRASCALE_SDK_NUPKG_SHA256)
if [[ "$regression" != true ]]; then
  required+=(TERRASCALE_API_URL TERRASCALE_AUTH_URL TERRASCALE_PROJECT_ID TERRASCALE_DATABASE_ID TERRASCALE_API_KEY TERRASCALE_JOURNEY_RUN_ID)
fi
for name in "${required[@]}"; do
  if [[ -z "${!name:-}" ]]; then echo "missing required input: $name" >&2; exit 1; fi
done
if [[ "$regression" != true ]]; then
  [[ "$TERRASCALE_JOURNEY_RUN_ID" =~ ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$ ]] || { echo 'invalid journey run ID' >&2; exit 1; }
fi
[[ -f "$TERRASCALE_SDK_NUPKG" ]] || { echo 'SDK nupkg is not a regular file' >&2; exit 1; }
TERRASCALE_SDK_NUPKG="$(realpath -- "$TERRASCALE_SDK_NUPKG")"
actual="$(sha256sum "$TERRASCALE_SDK_NUPKG" | awk '{print $1}')"
[[ "$actual" == "${TERRASCALE_SDK_NUPKG_SHA256,,}" ]] || { echo 'SDK nupkg SHA-256 mismatch' >&2; exit 1; }
command -v unzip >/dev/null || { echo 'unzip is required to inspect SDK nupkg' >&2; exit 1; }
nuspec="$(unzip -Z1 "$TERRASCALE_SDK_NUPKG" | awk '/\.nuspec$/ { print; exit }')"
[[ -n "$nuspec" ]] || { echo 'SDK nupkg has no nuspec' >&2; exit 1; }
metadata="$(unzip -p "$TERRASCALE_SDK_NUPKG" "$nuspec")"
grep -Eq '<id>TerraScale\.DotNet</id>' <<<"$metadata" || { echo 'SDK nupkg has unexpected package ID' >&2; exit 1; }
grep -Eq '<version>0\.1\.0</version>' <<<"$metadata" || { echo 'SDK nupkg has unexpected package version' >&2; exit 1; }
tmp="$(mktemp -d "${TMPDIR:-/tmp}/terrascale-dotnet-live.XXXXXX")"
trap 'rm -rf "$tmp"' EXIT
feed="$tmp/feed"; mkdir -p "$feed"; cp -- "$TERRASCALE_SDK_NUPKG" "$feed/TerraScale.DotNet.0.1.0.nupkg"
copied="$(sha256sum "$feed/TerraScale.DotNet.0.1.0.nupkg" | awk '{print $1}')"
[[ "$copied" == "$actual" ]] || { echo 'SDK nupkg changed while copying' >&2; exit 1; }
config="$tmp/NuGet.Config"
printf '%s\n' '<configuration><packageSources><clear/><add key="local-sdk" value="'"$feed"'"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="local-sdk"><package pattern="TerraScale.DotNet"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping></configuration>' > "$config"
if [[ "$regression" == true ]]; then
  project="$root/scripts/qualification/tests/Regression.csproj"
  dotnet restore "$project" --configfile "$config" --packages "$tmp/packages" -p:BaseIntermediateOutputPath="$tmp/obj/" -p:MSBuildProjectExtensionsPath="$tmp/obj/"
  dotnet run --project "$project" --configuration Release --no-restore -p:BaseIntermediateOutputPath="$tmp/obj/" -p:MSBuildProjectExtensionsPath="$tmp/obj/" -p:OutputPath="$tmp/out/"
else
  dotnet restore "$root/scripts/qualification/Qualification.csproj" --configfile "$config" --packages "$tmp/packages" -p:BaseIntermediateOutputPath="$tmp/obj/" -p:MSBuildProjectExtensionsPath="$tmp/obj/"
  dotnet run --project "$root/scripts/qualification/Qualification.csproj" --configuration Release --no-restore -p:BaseIntermediateOutputPath="$tmp/obj/" -p:MSBuildProjectExtensionsPath="$tmp/obj/" -p:OutputPath="$tmp/out/" -- --phase "$phase"
fi
