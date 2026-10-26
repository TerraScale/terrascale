#!/usr/bin/env bash
# Production endpoint replication matrix (RT6): documents, SQL HTTP, Postgres, Hrana and Wire in gru, sjc and fra.
#
# Run on a ts-orch host with endpoint-replication-proof.py beside this script:
#   scp scripts/terrabase/endpoint-replication-proof.{sh,py} ts-sjc-1:rt6-matrix/
#   ssh ts-sjc-1 'rt6-matrix/endpoint-replication-proof.sh <target-sha>'
# As the platform operator on the internal listeners (NetBird, h2c, the shared peer credential read from the local
# TerraBase container into a private header file, never printed or passed on a command line), it creates a dedicated
# user's organization, project, database and database-scoped key, runs the matrix and deletes them afterwards.
# Results go to rt6-matrix/<UTC time>.jsonl. Exits 0 when every check held, 1 otherwise.
set -euo pipefail
sha=${1:?usage: endpoint-replication-proof.sh <target-sha>}
here=$(cd "$(dirname "$0")" && pwd)
exec 9> "$HOME/.endpoint-replication-proof.lock"
flock -n 9 || { echo "another proof run holds $HOME/.endpoint-replication-proof.lock" >&2; exit 1; }
umask 077
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

# The running TerraBase task: its container's image is ghcr.io/terrascale/terrabase.
container=$(join <(sudo -n ctr -n terrascale containers ls | awk '$2 ~ /terrascale\/terrabase@/ {print $1}' | sort) \
  <(sudo -n ctr -n terrascale tasks ls | awk '$3 == "RUNNING" {print $1}' | sort) | head -1)
pid=$(sudo -n ctr -n terrascale tasks ls | awk -v c="$container" '$1 == c {print $2}')
echo "image $(sudo -n ctr -n terrascale containers ls | awk -v c="$container" '$1 == c {print $2}')"
secret_file=$(sudo -n cat "/proc/$pid/environ" | tr '\0' '\n' | sed -n 's/^TERRABASE_PEER_SECRET_FILE=//p')
# printf is a builtin: the credential reaches only the header file, never a process's arguments.
printf 'X-TerraBase-Peer-Credential: %s\n' "$(sudo -n cat "/proc/$pid/root$secret_file")" > "$work/peer"
sudo -n cat "/proc/$pid/root/run/ts-orch/peers/peers" > "$work/peers"
user=$(python3 -c 'import os,time
a="0123456789ABCDEFGHJKMNPQRSTVWXYZ"; n=(int(time.time()*1000)<<80)|int.from_bytes(os.urandom(10),"big")
print("".join(a[(n>>(5*i))&31] for i in range(25,-1,-1)))')
echo "X-TerraBase-User-Id: $user" > "$work/user"
echo "user $user"

nice -n 19 python3 -I "$here/endpoint-replication-proof.py" "$work" "$sha" "$here/$(date -u +%Y%m%dT%H%M%SZ).jsonl"
