#!/usr/bin/env bash
# Exercise scripts/ci/publish-binary.sh against stub git, dotnet and fj: the
# ref, revision, repository and token guards, token isolation from the build,
# the fresh release, an untouched complete release, the repair of a partial or
# misdirected one, a failed download or lookup that changes nothing, the
# GITHUB_TOKEN fallback, and failed, corrupted or substituted uploads.
set -euo pipefail
# The CI runner exports its own GITHUB_* and FORGEJO_* values (repository, ref, sha, token);
# drop them so every case sees only the fixtures set below.
unset "${!GITHUB_@}" "${!FORGEJO_@}"
repo=$(cd "$(dirname "$0")/.." && pwd)
sandbox=$(mktemp -d)
trap 'rm -rf -- "$sandbox"' EXIT
mkdir -p "$sandbox/repo/scripts/ci" "$sandbox/bin" "$sandbox/state/assets"
cp "$repo/scripts/ci/publish-binary.sh" "$repo/scripts/ci/stage-release.sh" "$sandbox/repo/scripts/ci/"
commit=$(printf '%040d' 1)
token=fixture-job-token-6d1e

# The arm64 smoke check records which binary it was given; FAIL_SMOKE makes it fail.
cat > "$sandbox/repo/scripts/ci/smoke-arm64.sh" <<'STUB'
#!/usr/bin/env bash
printf 'smoke %s\n' "$1" >> "$CALLS"
[[ ${FAIL_SMOKE:-} != 1 ]]
STUB
cat > "$sandbox/bin/git" <<'STUB'
#!/usr/bin/env bash
printf '%040d\n' 1
STUB
# Each publish writes a distinct aarch64 ELF stub, like a non-reproducible rebuild.
cat > "$sandbox/bin/dotnet" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
env >> "$STATE/dotnet-env"
case "$1" in
  msbuild) echo 0.1.0 ;;
  publish)
    while (($#)); do if [[ $1 == --output ]]; then out=$2; fi; shift; done
    mkdir -p "$out"
    { printf '\x7fELF'; printf '\0%.0s' {1..14}; printf '\xb7\x00'; head -c 32 /dev/urandom; } > "$out/ts-auth"
    chmod +x "$out/ts-auth" ;;
esac
STUB
cat > "$sandbox/bin/fj" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$CALLS"
case "$1 $2" in
  'auth add-token') cat > "$STATE/token" ;;
  'release create') touch "$STATE/release" ;;
  'release asset')
    case $3 in
      create)
        [[ ${FAIL_UPLOAD:-} != "$6" ]] || exit 1
        if [[ -n ${SUBSTITUTE_UPLOADS:-} ]]; then
          cp "$SUBSTITUTE_UPLOADS/$6" "$STATE/assets/$6"
        else
          cp "$5" "$STATE/assets/$6"
        fi
        [[ ${CORRUPT_UPLOAD:-} != "$6" ]] || printf 'x' >> "$STATE/assets/$6" ;;
      delete) rm -f "$STATE/assets/$5" ;;
      download)
        [[ ${FAIL_DOWNLOAD:-} != "$5" ]] || exit 1
        while (($#)); do if [[ $1 == -o ]]; then out=$2; fi; shift; done
        name=$(basename "$out")
        [[ -e $STATE/assets/$name ]] && cp "$STATE/assets/$name" "$out" ;;
    esac ;;
esac
STUB
# The releases API: 404 without a release, else its asset names. The token must arrive in a
# header file, never in argv (which is recorded).
cat > "$sandbox/bin/curl" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf 'curl %s\n' "$*" >> "$CALLS"
[[ ${FAIL_LIST:-} != 1 ]] || exit 7
while (($#)); do
  case $1 in
    --output) out=$2; shift ;;
    --header) header=${2#@}; shift ;;
  esac
  shift
done
grep -Fxq "Authorization: token $(cat "$STATE/token")" "$header"
if [[ -e $STATE/release ]]; then
  (cd "$STATE/assets" && find . -maxdepth 1 -type f -printf '%f\n') | jq -R . | jq -s '{assets: map({name: .})}' > "$out"
  printf 200
else
  printf 404
fi
STUB
chmod +x "$sandbox/bin/"*
export PATH="$sandbox/bin:$PATH" CALLS="$sandbox/calls" STATE="$sandbox/state"
export FORGEJO_REF=refs/heads/main FORGEJO_SHA=$commit FORGEJO_REPOSITORY=TerraScale/ts-auth FORGEJO_TOKEN=$token
publish="$sandbox/repo/scripts/ci/publish-binary.sh"
tag="v0.1.0-dev.$commit"

reset() { rm -rf -- "$CALLS" "$STATE" && mkdir -p "$STATE/assets"; }
reject() {
  reset
  if "$@" > "$sandbox/rejected" 2>&1; then echo "accepted invalid publish input: $*" >&2; exit 1; fi
  if [[ -s $CALLS ]]; then echo "fj ran despite invalid input: $*" >&2; exit 1; fi
}
verified() { (cd "$STATE/assets" && sha256sum --check --strict --quiet ts-auth-linux-arm64.sha256); }
called() { grep -Eq "$1" "$CALLS"; }

reject bash "$publish" extra
reject env FORGEJO_REF=refs/heads/feature bash "$publish"
reject env FORGEJO_REF=refs/pull/7/head bash "$publish"
reject env -u FORGEJO_REF bash "$publish"
reject env FORGEJO_SHA="$(printf '%040d' 2)" bash "$publish"
reject env -u FORGEJO_SHA bash "$publish"
reject env FORGEJO_REPOSITORY='../evil' bash "$publish"
reject env -u FORGEJO_REPOSITORY bash "$publish"
reject env -u FORGEJO_TOKEN bash "$publish"

# A fresh release: binary first, checksum last, and the published pair verifies.
reset
bash "$publish" > /dev/null
verified
[[ $(cat "$STATE/token") == "$token" ]]
called "^release create $tag .*--create-tag --branch $commit"
# The smoke check ran, on the staged binary, before anything reached the release.
[[ $(grep -n '' "$CALLS" | grep -m1 -E '^[0-9]+:(smoke|release|curl)' | cut -d: -f2-) == "smoke "*"/stage/ts-auth" ]]
[[ $(grep '^release asset create' "$CALLS" | awk '{print $6}' | paste -sd,) == ts-auth-linux-arm64,ts-auth-linux-arm64.sha256 ]]
# The token reaches fj on stdin only and never the build environment.
if grep -Fq "$token" "$CALLS" "$STATE/dotnet-env"; then echo "token leaked into argv or the build" >&2; exit 1; fi
mkdir "$sandbox/substitute"
cp "$STATE/assets/"* "$sandbox/substitute/"

# A complete release is left untouched.
: > "$CALLS"
bash "$publish" > /dev/null
verified
if called '^release (create|asset (create|delete))'; then echo "rewrote a complete release" >&2; exit 1; fi

# A partial release (checksum missing after an interrupted run) is repaired.
rm -f "$STATE/assets/ts-auth-linux-arm64.sha256"
: > "$CALLS"
bash "$publish" > /dev/null
verified
called '^release asset delete'
if called '^release create'; then echo "recreated an existing release" >&2; exit 1; fi

# A checksum file that names another path (here an empty /dev/null) does not vouch for a
# corrupt binary: the rerun replaces both assets.
printf 'corrupt' > "$STATE/assets/ts-auth-linux-arm64"
printf '%s  /dev/null\n' "$(sha256sum < /dev/null | awk '{print $1}')" > "$STATE/assets/ts-auth-linux-arm64.sha256"
: > "$CALLS"
bash "$publish" > /dev/null
verified
[[ $(wc -l < "$STATE/assets/ts-auth-linux-arm64.sha256") -eq 1 ]]
called '^release asset delete'

# A download that fails on a complete release is not evidence of damage: nothing is deleted.
: > "$CALLS"
if FAIL_DOWNLOAD=ts-auth-linux-arm64 bash "$publish" > "$sandbox/failed" 2>&1; then
  echo "a failed download passed" >&2
  exit 1
fi
verified
if called '^release asset (create|delete)'; then echo "changed a release it could not read" >&2; exit 1; fi

# Neither does a release lookup that fails.
: > "$CALLS"
if FAIL_LIST=1 bash "$publish" > "$sandbox/failed" 2>&1; then
  echo "a failed lookup passed" >&2
  exit 1
fi
if called '^release (create|asset (create|delete))'; then echo "changed a release it could not look up" >&2; exit 1; fi

# GITHUB_TOKEN alone is accepted and, like FORGEJO_TOKEN, never reaches the build.
reset
env -u FORGEJO_TOKEN GITHUB_TOKEN="$token" bash "$publish" > /dev/null
verified
if grep -Fq "$token" "$CALLS" "$STATE/dotnet-env"; then echo "GITHUB_TOKEN leaked into argv or the build" >&2; exit 1; fi

# A binary that does not start under emulation is never published.
reset
if FAIL_SMOKE=1 bash "$publish" > "$sandbox/failed" 2>&1; then
  echo "an arm64 binary that failed its smoke check was published" >&2
  exit 1
fi
if called '^(release|curl)'; then echo "touched the release after a failed smoke check" >&2; exit 1; fi

# A failed upload fails the run.
reset
if FAIL_UPLOAD=ts-auth-linux-arm64.sha256 bash "$publish" > "$sandbox/failed" 2>&1; then
  echo "a failed upload passed" >&2
  exit 1
fi

# An upload the registry stored wrongly fails the final verification.
reset
if CORRUPT_UPLOAD=ts-auth-linux-arm64 bash "$publish" > "$sandbox/failed" 2>&1; then
  echo "a corrupted upload passed" >&2
  exit 1
fi

# A different valid pair cannot replace the exact bytes that passed the smoke check.
reset
if SUBSTITUTE_UPLOADS="$sandbox/substitute" bash "$publish" > "$sandbox/failed" 2>&1; then
  echo "substituted uploads passed despite differing from the smoke-checked files" >&2
  exit 1
fi
verified
grep -Fq 'do not match the smoke-checked files' "$sandbox/failed"

echo "binary publish guards passed"
