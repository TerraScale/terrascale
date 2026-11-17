#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 3 ]]; then
  echo "usage: ssh <home writer host> 'bash -s' -- ORGANIZATION PROJECT DATABASE < scripts/terrabase/home-lease-repair.sh" >&2
  exit 2
fi
organization=$1 project=$2 database=$3
for id in "$organization" "$project" "$database"; do
  [[ $id =~ ^[0-9A-HJKMNP-TV-Z]{26}$ ]] || { echo "every id must be a canonical uppercase ULID" >&2; exit 2; }
done
umask 077
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
container=$(join <(sudo -n ctr -n terrascale containers ls | awk '$2 ~ /terrascale\/terrabase@/ {print $1}' | sort) \
  <(sudo -n ctr -n terrascale tasks ls | awk '$3 == "RUNNING" {print $1}' | sort) | head -1)
pid=$(sudo -n ctr -n terrascale tasks ls | awk -v c="$container" '$1 == c {print $2}')
environ=$(sudo -n cat "/proc/$pid/environ" | tr '\0' '\n')
secret_file=$(sed -n 's/^TERRABASE_PEER_SECRET_FILE=//p' <<< "$environ")
node=$(sed -n 's/^TS_NODE=//p' <<< "$environ")
printf 'X-TerraBase-Peer-Credential: %s\n' "$(sudo -n cat "/proc/$pid/root$secret_file")" > "$work/headers"
address=$(sudo -n cat "/proc/$pid/root/run/ts-orch/peers/peers" | awk -v n="$node" '$1 == n {print $3}')
[[ -n $address ]] || { echo "node $node is not in its peers file" >&2; exit 1; }
status=$(curl -sS -m 60 --http2-prior-knowledge -H @"$work/headers" -X POST -o "$work/answer" -w '%{http_code}' \
  "http://$address:8081/admin/organizations/$organization/projects/$project/databases/$database/home-lease")
cat "$work/answer"
echo
[[ $status == 200 ]]
