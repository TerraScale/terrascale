#!/usr/bin/env bash
# Moves a region's TerraBase machine onto a fresh, empty volume when the new build cannot use its data directory (storage
# format changed; greenfield has no migrations). The user's standing approval (2026-10-12) holds only while
# ops/fly/greenfield-no-customer-data exists: without it this refuses and the deploy fails. deploy.sh runs it:
# - planned, with --image: its gate found the region's on-disk format differs from the new image's (StorageFormat.Id);
# - after a failure, without --image: only when the machine logged the storage-format refusal.
# Order: stop the machine, snapshot the old volume and wait until Fly reports the snapshot created (Fly's default
# retention), switch the stopped machine to the new image (planned), clone it (Fly gives the clone a new empty volume of
# the same size), check the clone runs with passing checks, destroy the old machine, then delete the old volume. The IDs
# are printed (and added to the job summary) for the spend ledger. --dry-run prints the plan and the cost, changes nothing.
#
#   scripts/fly/volume-reset.sh <app> <region> [--dry-run] [--image <image>]
set -euo pipefail

usage() { echo "usage: $0 <app> <region> [--dry-run] [--image <image>]" >&2; exit 2; }
test "$#" -ge 2 || usage
app=$1 region=$2 dry_run='' image=''
shift 2
while [[ $# -gt 0 ]]; do
  case $1 in
    --dry-run) dry_run=1; shift ;;
    --image) [[ $# -ge 2 ]] || usage; image=$2; shift 2 ;;
    *) usage ;;
  esac
done

guard=$(dirname "$0")/../../ops/fly/greenfield-no-customer-data
# The end of every refusal of a data directory (SqliteStorageEngine: another storage format, the pre-tenancy layout).
refusal="start with an empty data directory (no automatic migration)"
fail() { echo "volume-reset: $*" >&2; exit 1; }

[[ -f $guard ]] || fail "refusing: $guard is gone, so TerraBase may hold customer data; migrate the data instead"

ids=$(flyctl machine list -a "$app" --json | jq -r --arg r "$region" '.[] | select(.region == $r) | .id')
[[ $(wc -w <<<"$ids") -eq 1 ]] || fail "expected one $app machine in $region, found: ${ids:-none}"
id=$ids
volume=$(flyctl machine list -a "$app" --json | jq -r --arg id "$id" '.[] | select(.id == $id) | .config.mounts[0].volume // empty')
[[ -n $volume ]] || fail "$app $region ($id) has no volume"
size=$(flyctl volumes show "$volume" -a "$app" --json | jq -r .size_gb)
if [[ -z $image ]]; then
  # Read the logs whole: grep -q would close the pipe early and pipefail would report flyctl's SIGPIPE as no match.
  logs=$(flyctl logs -a "$app" --machine "$id" --no-tail)
  # Fly keeps a short log buffer: `flyctl machine start <id>` makes the machine log its refusal again (it changes no data).
  grep -qF "$refusal" <<<"$logs" \
    || fail "$app $region ($id) has not recently logged the storage-format refusal; nothing to reset"
fi

echo "Plan: snapshot $volume, move $app $region ($id) onto a new empty volume${image:+ with $image}, then delete $volume."
echo "Cost: a new ${size} GB volume replaces $volume; the snapshot is billed until Fly's retention expires."
if [[ -n $dry_run ]]; then
  echo "Dry run: nothing changed."
  exit 0
fi

# A stopped machine gives a consistent snapshot; the region is down until the clone serves. Stopping a stopped machine
# (one that hit its restart limit) is not a failure.
flyctl machine stop "$id" -a "$app" || true
snapshots() { flyctl volumes snapshots list "$volume" -a "$app" --json | jq -r '.[] | select(.status == "created" and .size > 0) | .id'; }
before=$(snapshots)
flyctl volumes snapshots create "$volume" -a "$app"
snapshot=
for _ in $(seq 60); do
  snapshot=$(comm -13 <(sort <<<"$before") <(snapshots | sort) | head -n1)
  [[ -n $snapshot ]] && break
  sleep 10
done
[[ -n $snapshot ]] || fail "snapshot of $volume not created after 10 min; $id is stopped, nothing else changed"
echo "Snapshot $snapshot of $volume created"

if [[ -n $image ]]; then
  flyctl machine update "$id" -a "$app" --image "$image" --skip-start --yes
fi
flyctl machine clone "$id" -a "$app" --region "$region" --volume-requires-unique-zone=false
new=$(flyctl machine list -a "$app" --json | jq -r --arg r "$region" --arg id "$id" \
  '.[] | select(.region == $r and .id != $id and .state == "started" and ([.checks[]?.status] | all(. == "passing"))) | "\(.id) \(.config.mounts[0].volume)"')
[[ -n $new && $(wc -l <<<"$new") -eq 1 ]] || fail "no started clone with passing checks in $region; $id and $volume left as they are"
flyctl machine destroy "$id" -a "$app" --force
[[ $(flyctl volumes show "$volume" -a "$app" --json | jq -r '.attached_machine_id // "detached"') == detached ]] \
  || fail "$volume is still attached; not deleted"
flyctl volumes destroy "$volume" -a "$app" --yes
line="$app $region volume reset: machine $id -> ${new% *}, volume $volume (deleted) -> ${new#* } (${size} GB), snapshot $snapshot"
echo "$line"
if [[ -n ${GITHUB_STEP_SUMMARY:-} ]]; then echo "- $line" >>"$GITHUB_STEP_SUMMARY"; fi
