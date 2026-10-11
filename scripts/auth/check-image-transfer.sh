#!/usr/bin/env bash
# Exercise the CI transfer commands against private registry/tool fixtures.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
sandbox=$(mktemp -d)
trap 'rm -rf -- "$sandbox"' EXIT
mkdir -p "$sandbox/repo/scripts/auth/ci" "$sandbox/bin" "$sandbox/temp"
cp "$repo/scripts/auth/ci/transfer-image.sh" "$repo/scripts/auth/ci/image-ref.sh" "$sandbox/repo/scripts/auth/ci/"
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
  'push --digestfile')
    if [[ ${INTERRUPT_STAGE:-} == 1 ]]; then kill -TERM "$PPID"; sleep 1; exit 0; fi
    printf '%s\n' "${PUSH_DIGEST:-$IMAGE_MANIFEST_DIGEST}" > "$3" ;;
esac
STUB
cat > "$sandbox/bin/skopeo" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$CALLS"
case "$1" in
  copy)
    [[ $2 == --all && $3 == --preserve-digests ]] || exit 1
    [[ $4 == --src-authfile && $5 == "$REGISTRY_AUTH_FILE" ]] || exit 1
    [[ $6 == --dest-authfile && $7 == "$REGISTRY_AUTH_FILE" ]] || exit 1
    [[ ${FAIL_COPY:-} != 1 ]] || exit 1
    while (($#)); do
      if [[ $1 == --digestfile ]]; then
        printf '%s\n' "${COPY_DIGEST:-$IMAGE_MANIFEST_DIGEST}" > "$2"
        break
      fi
      shift
    done
    ;;
  list-tags) [[ ${FAIL_LIST_TAGS:-} != 1 ]] || exit 1; cat "$TAGS" ;;
  inspect)
    # The very old run-2 tag aliases a recent run-2 tag and must survive.
    if [[ ${*: -1} == *-2-1-* ]]; then printf 'sha256:%064d\n' 5; else printf 'sha256:%064d\n' 4; fi ;;
esac
STUB
chmod +x "$sandbox/bin/"*
export PATH="$sandbox/bin:$PATH" CALLS="$sandbox/calls" TAGS="$sandbox/tags"
export RUNNER_TEMP="$sandbox/temp" REPOSITORY_OWNER=TerraScale REGISTRY_TOKEN=fixture-token REGISTRY_USER=fixture-bot
export GITHUB_RUN_ID=42 GITHUB_RUN_ATTEMPT=1 GITHUB_OUTPUT="$sandbox/output"
IMAGE_MANIFEST_DIGEST="sha256:$(printf '%064d' 3)"
export IMAGE_MANIFEST_DIGEST
transfer="$sandbox/repo/scripts/auth/ci/transfer-image.sh"
ref="$sandbox/repo/scripts/auth/ci/image-ref.sh"
reject() {
  if "$@" > "$sandbox/rejected" 2>&1; then echo "accepted invalid transfer input" >&2; exit 1; fi
}
reject bash "$transfer"
reject bash "$transfer" bad
reject bash "$transfer" stage extra
reject env REPOSITORY_OWNER='../bad' bash "$transfer" stage
for owner in org- org_. org..name org___name; do
  reject env REPOSITORY_OWNER="$owner" bash "$transfer" stage
done
reject env IMAGE_MANIFEST_DIGEST=bad bash "$transfer" publish
reject env -u REGISTRY_TOKEN bash "$transfer" stage
reject env -u REGISTRY_USER bash "$transfer" stage
reject env -u GITHUB_RUN_ID bash "$transfer" stage
reject env -u GITHUB_OUTPUT bash "$transfer" stage
reject env -u RUNNER_TEMP bash "$transfer" stage
reject env FAIL_GIT=1 bash "$ref"
reject bash "$ref" extra
bash "$transfer" stage
grep -Fx "image-digest=$IMAGE_MANIFEST_DIGEST" "$GITHUB_OUTPUT"
candidate=$(awk '$1 == "tag" {print $3; exit}' "$CALLS")
[[ $candidate =~ ^git\.terrascale\.tech/terrascale/ts-auth-ci:[0-9a-f]{40}-[0-9]+-[0-9]+-([0-9]{10})$ ]]
bash "$transfer" publish
bash "$transfer" publish
commit=$(printf '%040d' 1)
awk -v ref="docker://git.terrascale.tech/terrascale/ts-auth:$commit" \
  -v source="docker://git.terrascale.tech/terrascale/ts-auth-ci@$IMAGE_MANIFEST_DIGEST" \
  '$1 == "copy" && $2 == "--all" && $3 == "--preserve-digests" && $(NF-1) == source && $NF == ref {found++} END {exit found != 2}' "$CALLS"
reject env PUSH_DIGEST=invalid bash "$transfer" stage
reject env FAIL_COPY=1 bash "$transfer" publish
test -z "$(find "$RUNNER_TEMP" -mindepth 1 -print -quit)"
reject env COPY_DIGEST="sha256:$(printf '%064d' 4)" bash "$transfer" publish
grep -F 'published manifest digest does not match the staged digest' "$sandbox/rejected" >/dev/null
test -z "$(find "$RUNNER_TEMP" -mindepth 1 -print -quit)"
# Single and double underscores are valid registry component separators.
for owner in org_name org__name; do
  REPOSITORY_OWNER="$owner" bash "$transfer" publish
done
reject env INTERRUPT_STAGE=1 bash "$transfer" stage
test -z "$(find "$RUNNER_TEMP" -type f -print -quit)"
grep -F 'login git.terrascale.tech --username fixture-bot --password-stdin' "$CALLS" >/dev/null
old=$(($(date +%s) - 31 * 86400))
recent=$(date +%s)
jq -n --arg expired "$commit-1-1-$old" --arg alias "$commit-2-1-$old" --arg recent "$commit-2-1-$recent" \
  '{Tags:[$expired,$alias,$recent,"unmanaged"]}' > "$TAGS"
# An unmanaged tag is protected; give it the alias digest, not the expired one.
sed -i 's/\*-2-1-\*/\*-2-1-* || ${*: -1} == *:unmanaged/' "$sandbox/bin/skopeo"
bash "$transfer" prune
test "$(grep -c '^delete ' "$CALLS")" -eq 1
awk -v expired="docker://git.terrascale.tech/terrascale/ts-auth-ci:$commit-1-1-$old" \
  '$1 == "delete" {if ($NF != expired) exit 1; found++} END {if (found != 1) exit 1}' "$CALLS"
test -z "$(find "$RUNNER_TEMP" -type f -print -quit)"
printf '{"Tags":[]}\n' > "$TAGS"
bash "$transfer" prune
reject env FAIL_LIST_TAGS=1 bash "$transfer" prune
# Freeze time so the exact cutoff boundary remains protected. Use the tag
# emitted by stage to couple retention to the producer's actual grammar.
stage_time=${candidate##*-}
cat > "$sandbox/bin/date" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$FIXED_NOW"
STUB
chmod +x "$sandbox/bin/date"
export FIXED_NOW=$((stage_time + 30 * 86400))
printf '{"Tags":["%s"]}\n' "${candidate##*:}" > "$TAGS"
: > "$CALLS"
bash "$transfer" prune
if grep -q '^delete ' "$CALLS"; then echo 'deleted candidate at cutoff' >&2; exit 1; fi
FIXED_NOW=$((FIXED_NOW + 1)) bash "$transfer" prune
test "$(grep -c '^delete ' "$CALLS")" -eq 1
printf '{"Tags":null}\n' > "$TAGS"
reject bash "$transfer" prune
test -z "$(find "$RUNNER_TEMP" -type f -print -quit)"
echo 'Image transfer guards, digest pinning, repeat publish, credential cleanup and retention passed'
