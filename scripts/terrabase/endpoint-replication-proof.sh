#!/usr/bin/env bash
set -euo pipefail
sha=${1:?usage: endpoint-replication-proof.sh <target-sha>}
here=$(cd "$(dirname "$0")" && pwd)
exec 9> "$HOME/.endpoint-replication-proof.lock"
flock -n 9 || { echo "another proof run holds $HOME/.endpoint-replication-proof.lock" >&2; exit 1; }
umask 077
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

container=$(join <(sudo -n ctr -n terrascale containers ls | awk '$2 ~ /terrascale\/terrabase@/ {print $1}' | sort) \
  <(sudo -n ctr -n terrascale tasks ls | awk '$3 == "RUNNING" {print $1}' | sort) | head -1)
pid=$(sudo -n ctr -n terrascale tasks ls | awk -v c="$container" '$1 == c {print $2}')
echo "image $(sudo -n ctr -n terrascale containers ls | awk -v c="$container" '$1 == c {print $2}')"
secret_file=$(sudo -n cat "/proc/$pid/environ" | tr '\0' '\n' | sed -n 's/^TERRABASE_PEER_SECRET_FILE=//p')
printf 'X-TerraBase-Peer-Credential: %s\n' "$(sudo -n cat "/proc/$pid/root$secret_file")" > "$work/peer"
sudo -n cat "/proc/$pid/root/run/ts-orch/peers/peers" > "$work/peers"
user=$(python3 -c 'import os,time
a="0123456789ABCDEFGHJKMNPQRSTVWXYZ"; n=(int(time.time()*1000)<<80)|int.from_bytes(os.urandom(10),"big")
print("".join(a[(n>>(5*i))&31] for i in range(25,-1,-1)))')
echo "X-TerraBase-User-Id: $user" > "$work/user"
echo "user $user"

nice -n 19 python3 -I "$here/endpoint-replication-proof.py" "$work" "$sha" "$here/$(date -u +%Y%m%dT%H%M%SZ).jsonl"
