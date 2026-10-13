#!/usr/bin/env bash
# Second session: PITR, the partition run, and the fault tests with the start-after-stopped shim. ./with-key.sh ./live-run-2.sh <tag>
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd); cd "$here"; tag=$1; source ips.env; export IPS
step() { echo "=== $(date -u +%T) $*"; }
summary() { grep -E "^\s+(total|failed|succeeded|skipped):" "$1" | tr -s ' \n' ' '; echo; }
step "PITR"; python3 -I counts.py pitr $tag-pitr 500 100
step "partition fra | gru+sjc"; python3 -I counts.py partition $tag-part 300
for t in Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns A_restarted_region_replays_its_backlog_to_a_region_that_returns Restart_in_the_middle_of_replication_loses_no_write; do
  step "fault (shim) $t"; PATH="$here/shim:$PATH" TERRABASE_E2E_FLY_OUTAGE=1 ./run.sh $tag-fault-$t.log --treenode-filter "/*/*/ThreeRegionFaultE2ETests/$t*" --output Detailed; summary $tag-fault-$t.log
done
step "done"
