#!/usr/bin/env bash
# Pre-freeze live run (e1812ce3), one key for the session: ./with-key.sh ./live-run-3.sh <tag>
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd); cd "$here"; tag=$1; source ips.env; export IPS
step() { echo "=== $(date -u +%T) $*"; }
summary() { grep -E "^\s+(total|failed|succeeded|skipped):" "$1" | tr -s ' \n' ' '; echo; }
for i in 1 2 3; do step "six directions $i"; ./run.sh $tag-six-$i.log --treenode-filter '/*/*/ThreeRegionE2ETests/Write_in_one_region_is_readable_in_the_other_two*' --output Detailed; summary $tag-six-$i.log; done
for suite in ThreeRegionE2ETests LatticeE2ETests ReplicationE2ETests; do step "$suite"; ./run.sh $tag-$suite.log --treenode-filter "/*/*/$suite/*" --output Detailed; summary $tag-$suite.log; done
step "independent 3,000-document load"; python3 -I counts.py load $tag-bulk 3000
step "partition fra | gru+sjc with same-document edits"; python3 -I counts.py partition $tag-part 300
step "PITR"; python3 -I counts.py pitr $tag-pitr 500 100
for t in Region_down_then_back_catches_up_on_every_write_made_while_it_was_down Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns A_restarted_region_replays_its_backlog_to_a_region_that_returns Restart_in_the_middle_of_replication_loses_no_write; do
  step "fault (shim) $t"; PATH="$here/shim:$PATH" TERRABASE_E2E_FLY_OUTAGE=1 ./run.sh $tag-fault-$t.log --treenode-filter "/*/*/ThreeRegionFaultE2ETests/$t*" --output Detailed; summary $tag-fault-$t.log
done
step "FlyOutageE2ETests"; PATH="$here/shim:$PATH" TERRABASE_E2E_FLY_OUTAGE=1 ./run.sh $tag-flyoutage.log --treenode-filter '/*/*/FlyOutageE2ETests/*' --output Detailed; summary $tag-flyoutage.log
step "kill + clean restart per machine"; ./restarts.sh $tag-bulk 3000 2>&1 | grep -v "curl: (28)"
step "F7 + F3"; ./f3f7.sh
step "bulk after everything"; python3 -I counts.py verify $tag-bulk 3000
step "done"
