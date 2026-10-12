#!/usr/bin/env bash
# Moves a region's TerraBase machine onto a fresh, empty volume after the new build refused the old data directory
# (storage format changed; greenfield has no migrations). The user's standing approval (2026-10-12) holds only while
# ops/fly/greenfield-no-customer-data exists: without it this refuses and the deploy fails. deploy.sh runs it when a
# TerraBase region's deploy fails.
# Order: snapshot the old volume and wait until Fly reports the snapshot created (Fly's default retention), clone the
# machine (Fly gives the clone a new empty volume of the same size), check the clone runs with passing checks, destroy the
# old machine, then delete the old volume. The IDs are printed (and added to the job summary) for the spend ledger.
# --dry-run prints the plan and the cost, changes nothing.
#
#   scripts/fly/volume-reset.sh <app> <region> [--dry-run]
set -euo pipefail

usage() { echo "usage: $0 <app> <region> [--dry-run]" >&2; exit 2; }
case $# in
  2) dry_run= ;;
  3) [[ $3 == --dry-run ]] || usage; dry_run=1 ;;
  *) usage ;;
esac
app=$1 region=$2

guard=$(dirname "$0")/../../ops/fly/greenfield-no-customer-data
# SqliteStorageEngine.OtherFormat, verbatim.
format_error="data directory was written by another TerraBase layout or storage format; start with an empty data directory (no automatic migration)"
fail() { echo "volume-reset: $*" >&2; exit 1; }

[[ -f $guard ]] || fail "refusing: $guard is gone, so TerraBase may hold customer data; migrate the data instead"

ids=$(flyctl machine list -a "$app" --json | jq -r --arg r "$region" '.[] | select(.region == $r) | .id')
[[ $(wc -w <<<"$ids") -eq 1 ]] || fail "expected one $app machine in $region, found: ${ids:-none}"
id=$ids
volume=$(flyctl machine list -a "$app" --json | jq -r --arg id "$id" '.[] | select(.id == $id) | .config.mounts[0].volume // empty')
[[ -n $volume ]] || fail "$app $region ($id) has no volume"
size=$(flyctl volumes show "$volume" -a "$app" --json | jq -r .size_gb)
# Read the logs whole: grep -q would close the pipe early and pipefail would report flyctl's SIGPIPE as no match.
logs=$(flyctl logs -a "$app" --machine "$id" --no-tail)
# Fly keeps a short log buffer: `flyctl machine start <id>` makes the machine log its refusal again (it changes no data).
grep -qF "$format_error" <<<"$logs" \
  || fail "$app $region ($id) has not recently logged the storage-format refusal; nothing to reset"

echo "Plan: snapshot $volume, move $app $region ($id) onto a new empty volume, then delete $volume."
echo "Cost: a new ${size} GB volume replaces $volume; the snapshot is billed until Fly's retention expires."
if [[ -n $dry_run ]]; then
  echo "Dry run: nothing changed."
  exit 0
fi

snapshots() { flyctl volumes snapshots list "$volume" -a "$app" --json | jq -r '.[] | select(.status == "created" and .size > 0) | .id'; }
before=$(snapshots)
flyctl volumes snapshots create "$volume" -a "$app"
snapshot=
for _ in $(seq 60); do
  snapshot=$(comm -13 <(sort <<<"$before") <(snapshots | sort) | head -n1)
  [[ -n $snapshot ]] && break
  sleep 10
done
[[ -n $snapshot ]] || fail "snapshot of $volume not created after 10 min; nothing else changed"
echo "Snapshot $snapshot of $volume created"

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
