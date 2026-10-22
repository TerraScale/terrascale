#!/usr/bin/env bash
# Applies 25 ms per loopback traversal only inside a disposable network namespace (~50 ms RTT).
set -euo pipefail
if [[ $# -eq 0 ]]; then
  echo 'usage: r4-netem.sh command [args...]' >&2
  exit 2
fi

# Enter isolation before any ip/tc operation; setup failure never changes the host loopback.
exec unshare --user --map-root-user --net bash -c '
set -euo pipefail
ip link set lo up
tc qdisc add dev lo root netem delay 25ms
tc -s qdisc show dev lo
exec "$@"
' r4-netem "$@"
