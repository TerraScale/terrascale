#!/usr/bin/env bash
# Exercise the CI transfer commands against private registry/tool fixtures.
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
sandbox=$(mktemp -d)
trap 'rm -rf -- "$sandbox"' EXIT
mkdir -p "$sandbox/repo/scripts/ci" "$sandbox/bin" "$sandbox/temp"
cp "$repo/scripts/ci/transfer-image.sh" "$repo/scripts/ci/image-ref.sh" "$sandbox/repo/scripts/ci/"
cat > "$sandbox/bin/git" <<'STUB'
#!/usr/bin/env bash
[[ ${FAIL_GIT:-} != 1 ]] || exit 1
printf '%040d\n' 1
STUB
cat > "$sandbox/bin/podman" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$CALLS"
case "$1 $2" in
  'login git.terrascale.tech') cat >/dev/null; printf '{}\n' > "$REGISTRY_AUTH_FILE" ;;
  'pull --quiet') printf '%064d\n' 2 ;;
  'image inspect') printf '%064d\n' "${WRONG_ID:-2}" ;;
  'push --digestfile')
    if [[ ${INTERRUPT_STAGE:-} == 1 ]]; then kill -TERM "$PPID"; sleep 1; exit 0; fi
    printf 'sha256:%064d\n' 3 > "$3" ;;
esac
STUB
cat > "$sandbox/bin/skopeo" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$CALLS"
case "$1" in
  list-tags) cat "$TAGS" ;;
  inspect)
    # The very old run-2 tag aliases a recent run-2 tag and must survive.
    if [[ ${*: -1} == *-2-1-* ]]; then printf 'sha256:%064d\n' 5; else printf 'sha256:%064d\n' 4; fi ;;
esac
STUB
chmod +x "$sandbox/bin/"*
export PATH="$sandbox/bin:$PATH" CALLS="$sandbox/calls" TAGS="$sandbox/tags"
export RUNNER_TEMP="$sandbox/temp" REPOSITORY_OWNER=TerraScale REGISTRY_PUSH_TOKEN=fixture-token
export GITHUB_RUN_ID=42 GITHUB_RUN_ATTEMPT=1 GITHUB_OUTPUT="$sandbox/output"
IMAGE_MANIFEST_DIGEST="sha256:$(printf '%064d' 3)"
export IMAGE_MANIFEST_DIGEST
transfer="$sandbox/repo/scripts/ci/transfer-image.sh"
ref="$sandbox/repo/scripts/ci/image-ref.sh"
reject() {
  if "$@" > "$sandbox/rejected" 2>&1; then echo "accepted invalid transfer input" >&2; exit 1; fi
}
reject bash "$transfer"
reject bash "$transfer" bad
reject bash "$transfer" stage extra
reject env REPOSITORY_OWNER='../bad' bash "$transfer" stage
reject env IMAGE_MANIFEST_DIGEST=bad bash "$transfer" publish
reject env -u REGISTRY_PUSH_TOKEN bash "$transfer" stage
reject env FAIL_GIT=1 bash "$ref"
reject bash "$ref" extra
bash "$transfer" stage
grep -Fx "image-digest=$IMAGE_MANIFEST_DIGEST" "$GITHUB_OUTPUT"
bash "$transfer" publish
bash "$transfer" publish
reject env WRONG_ID=9 bash "$transfer" publish
reject env INTERRUPT_STAGE=1 bash "$transfer" stage
test -z "$(find "$RUNNER_TEMP" -type f -print -quit)"
grep -F 'login git.terrascale.tech --username terrascale --password-stdin' "$CALLS" >/dev/null
old=$(($(date +%s) - 31 * 86400))
recent=$(date +%s)
commit=$(printf '%040d' 1)
jq -n --arg expired "$commit-1-1-$old" --arg alias "$commit-2-1-$old" --arg recent "$commit-2-1-$recent" \
  '{Tags:[$expired,$alias,$recent,"unmanaged"]}' > "$TAGS"
# An unmanaged tag is protected; give it the alias digest, not the expired one.
sed -i 's/\*-2-1-\*/\*-2-1-* || ${*: -1} == *:unmanaged/' "$sandbox/bin/skopeo"
bash "$transfer" prune
test "$(grep -c '^delete ' "$CALLS")" -eq 1
grep -F "docker://git.terrascale.tech/terrascale/ts-auth-ci:$commit-1-1-$old" "$CALLS" >/dev/null
test -z "$(find "$RUNNER_TEMP" -type f -print -quit)"
printf '{"Tags":[]}\n' > "$TAGS"
bash "$transfer" prune
printf '{"Tags":null}\n' > "$TAGS"
reject bash "$transfer" prune
test -z "$(find "$RUNNER_TEMP" -type f -print -quit)"
echo 'Image transfer guards, digest pinning, repeat publish, credential cleanup and retention passed'
