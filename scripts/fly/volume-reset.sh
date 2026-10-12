#!/usr/bin/env bash
# Moves a region's TerraBase machine onto a fresh, empty volume after the new build refused the old data directory
# (storage format changed; greenfield has no migrations). Never automatic: a human passes --approve-volume-reset with the
# region, once per run, and the repo must still say it holds no customer data (ops/fly/greenfield-no-customer-data).
# Order: snapshot the old volume and wait until Fly reports the snapshot created, clone the machine (Fly gives the clone a
# new empty volume of the same size), destroy the old machine. The old volume is never deleted: it stays detached, and its
# ID and the snapshot ID are printed for a human to delete later. --dry-run prints the plan and the cost, changes nothing.
#
#   scripts/fly/volume-reset.sh <app> <region> --dry-run
#   scripts/fly/volume-reset.sh <app> <region> --approve-volume-reset <region>
#   gh run rerun <deploy run id> --failed    # then finish the rollout
set -euo pipefail

usage() { echo "usage: $0 <app> <region> (--dry-run | --approve-volume-reset <region>)" >&2; exit 2; }
test "$#" -ge 3 || usage
app=$1 region=$2 mode=$3 approved=${4:-}
case $mode in
  --dry-run) test "$#" -eq 3 || usage ;;
  --approve-volume-reset) test "$#" -eq 4 || usage ;;
  *) usage ;;
esac

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

echo "Plan: snapshot $volume, move $app $region ($id) onto a new empty volume, keep $volume detached."
echo "Cost: +${size} GB volume in $region; $volume and its snapshot stay billed until a human deletes them."
if [[ $mode == --dry-run ]]; then
  echo "Dry run: nothing changed. To approve: $0 $app $region --approve-volume-reset $region"
  exit 0
fi
[[ $approved == "$region" ]] || fail "refusing: the approval names '$approved', not $region"

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
flyctl machine destroy "$id" -a "$app" --force
new=$(flyctl machine list -a "$app" --json | jq -r --arg r "$region" '.[] | select(.region == $r) | "\(.id) \(.config.mounts[0].volume)"')
[[ $(flyctl volumes show "$volume" -a "$app" --json | jq -r '.attached_machine_id // "detached"') == detached ]] \
  || fail "$volume is still attached"
line="$app $region volume reset: machine $id -> ${new% *}, volume $volume (kept, detached) -> ${new#* }, snapshot $snapshot"
echo "$line"
if [[ -n ${GITHUB_STEP_SUMMARY:-} ]]; then echo "- $line" >>"$GITHUB_STEP_SUMMARY"; fi
