#!/usr/bin/env bash
# Publish the self-contained single-file linux-arm64 executable as
# ts-auth-linux-arm64, with its ts-auth-linux-arm64.sha256 in sha256sum format,
# as assets of the Forgejo release v<version>-dev.<commit>. ts-orchestrator
# downloads them to run ts-auth natively on arm64 hosts. CI runs this only on
# pushes to protected main, after fast and release passed.
#
# New uploads succeed only when both downloads equal the smoke-checked files. A
# rerun leaves a release whose assets verify untouched; one an interrupted run
# left with a missing asset, or with a checksum that does not match its
# executable, gets both assets replaced (binary first, checksum last), so a
# consumer that requires the checksum never accepts a partial upload. A lookup or
# download that fails changes nothing: it is not evidence of a broken release.
# The workflow's concurrency group runs one main pipeline at a time, so two
# publishers of the same tag never overlap.
#
# Environment: FORGEJO_TOKEN (job token with release write), FORGEJO_REF,
# FORGEJO_SHA and FORGEJO_REPOSITORY, or their GITHUB_* equivalents.
set -euo pipefail

test "$#" -eq 0 || { echo "usage: $0" >&2; exit 2; }

# The build below must never see the token: MSBuild exposes every environment
# variable as a property to all imported targets.
token=${FORGEJO_TOKEN:-${GITHUB_TOKEN:-}}
unset FORGEJO_TOKEN GITHUB_TOKEN
[[ -n $token ]] || { echo "FORGEJO_TOKEN (or GITHUB_TOKEN) is required" >&2; exit 2; }

host=git.terrascale.tech
asset=ts-auth-linux-arm64
ref=${FORGEJO_REF:-${GITHUB_REF:-}}
[[ $ref == refs/heads/main ]] || { echo "ref '$ref' is not refs/heads/main" >&2; exit 1; }
sha=${FORGEJO_SHA:-${GITHUB_SHA:-}}
repository=${FORGEJO_REPOSITORY:-${GITHUB_REPOSITORY:-}}
[[ $repository =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$ ]] || { echo "invalid repository '$repository'" >&2; exit 2; }

repo=$(cd "$(dirname "$0")/../.." && pwd)
cd "$repo"
commit=$(git rev-parse HEAD)
[[ $commit == "$sha" ]] || { echo "checkout $commit is not the event revision '$sha'" >&2; exit 1; }
version=$(dotnet msbuild src/TerraScale.Auth/TerraScale.Auth.csproj -getProperty:Version -nologo)
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+[0-9A-Za-z.-]*$ ]] || { echo "invalid version '$version'" >&2; exit 1; }
tag="v$version-dev.$commit"

umask 077
work=$(mktemp -d)
# The work directory holds the fj login; signals exit through the EXIT trap so it is removed.
trap 'rm -rf "$work"' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
bash scripts/ci/stage-release.sh "$work/stage" linux-arm64
# The exact bytes about to be published must start on arm64 (under emulation) first.
bash scripts/ci/smoke-arm64.sh "$work/stage/ts-auth"
mkdir -- "$work/assets"
install -m 0755 -- "$work/stage/ts-auth" "$work/assets/$asset"
(cd "$work/assets" && sha256sum -- "$asset" >"$asset.sha256" && sha256sum --check --strict -- "$asset.sha256")

# fj keeps its login under XDG_DATA_HOME. Point it at this script's temporary
# directory so the job token never lands in a reused home directory; the EXIT
# trap removes it. The token reaches fj on stdin only.
export XDG_DATA_HOME="$work/fj-data" XDG_CONFIG_HOME="$work/fj-config"
printf '%s\n' "$token" | fj auth add-token --host "$host" >/dev/null
printf 'Authorization: token %s\n' "$token" > "$work/auth-header"
unset token

# The release's asset names after "present", or "absent" when the API answers 404.
# Any other answer or a transport failure stops the run.
release_state() {
  local status
  status=$(curl --silent --show-error --output "$work/release.json" --write-out '%{http_code}' \
    --header @"$work/auth-header" "https://$host/api/v1/repos/$repository/releases/tags/$tag") || return 1
  case $status in
    404) echo absent ;;
    200) echo present && jq -r '.assets[].name' "$work/release.json" ;;
    *) echo "release lookup for $tag answered HTTP $status" >&2 && return 1 ;;
  esac
}

# Downloads both assets into a fresh check directory.
download_pair() {
  rm -rf -- "$work/check" && mkdir -- "$work/check" &&
    fj release asset download "$tag" "$asset" --host "$host" --repo "$repository" -o "$work/check/$asset" >/dev/null &&
    fj release asset download "$tag" "$asset.sha256" --host "$host" --repo "$repository" -o "$work/check/$asset.sha256" >/dev/null
}

# Whether the downloaded checksum file is exactly the one line sha256sum writes for the
# downloaded binary, so no other path or extra entry can satisfy it.
pair_matches() {
  [[ $(cat -- "$work/check/$asset.sha256") == "$(cd "$work/check" && sha256sum -- "$asset")" ]]
}

state=$(release_state) || { echo "could not read release $tag; nothing was changed" >&2; exit 1; }
if [[ $state == absent ]]; then
  fj release create "$tag" --host "$host" --repo "$repository" --create-tag --branch "$commit" \
    --body "ts-auth linux-arm64 single-file release for $commit"
else
  names=$(tail -n +2 <<< "$state")
  if grep -qxF -- "$asset" <<< "$names" && grep -qxF -- "$asset.sha256" <<< "$names"; then
    download_pair || { echo "could not download the assets of release $tag; nothing was changed" >&2; exit 1; }
    # Rebuilds need not be reproducible: an existing valid pair stays unchanged.
    if pair_matches; then
      echo "release $tag already holds a verified $asset; it is unchanged"
      exit 0
    fi
    echo "release $tag holds a checksum that does not match its executable; replacing both assets"
  else
    echo "release $tag is missing assets; replacing both"
  fi
  for name in "$asset.sha256" "$asset"; do
    if grep -qxF -- "$name" <<< "$names"; then
      fj release asset delete "$tag" "$name" --host "$host" --repo "$repository" >/dev/null
    fi
  done
fi

# The checksum goes last, so a present checksum implies its executable is too.
for name in "$asset" "$asset.sha256"; do
  fj release asset create "$tag" "$work/assets/$name" "$name" --host "$host" --repo "$repository"
  echo "uploaded $name"
done
download_pair || { echo "could not download the published assets of release $tag" >&2; exit 1; }
pair_matches || { echo "the published $asset does not verify against its checksum" >&2; exit 1; }
if ! cmp --silent "$work/assets/$asset" "$work/check/$asset" ||
  ! cmp --silent "$work/assets/$asset.sha256" "$work/check/$asset.sha256"; then
  echo "the published assets do not match the smoke-checked files" >&2
  exit 1
fi
printf 'published and verified %s and %s.sha256 in release %s\n' "$asset" "$asset" "$tag"
