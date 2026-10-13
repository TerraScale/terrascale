#!/usr/bin/env bash
# Follow-up session: the partition's shared document per region, PITR, and E9 repeats. ./with-key.sh ./live-run-4.sh <tag>
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd); cd "$here"; tag=$1
step() { echo "=== $(date -u +%T) $*"; }
step "shared document of the partition step (r6-part), per region"; python3 -I -u counts.py shared r6-part
step "PITR"; python3 -I -u counts.py pitr $tag-pitr 500 100
for i in 1 2 3 4 5; do step "E9 repeat $i"; ./run.sh $tag-e9-$i.log --treenode-filter '/*/*/LatticeE2ETests/E9*' --output Detailed; grep -E "^(passed|failed) |ScenarioException" $tag-e9-$i.log | cut -c1-200; done
step "done"
