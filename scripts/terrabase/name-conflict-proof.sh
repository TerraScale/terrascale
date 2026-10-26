#!/usr/bin/env bash
# Production proof that a database name conflict keeps both databases (TS-1120, docs/terrabase/database-name-conflicts.md).
#
# Run on a ts-orch host (ssh ts-sjc-1 'bash -s' < scripts/terrabase/name-conflict-proof.sh). A run outlives its ssh
# session, so a lock refuses a second one while it runs. As the platform operator on the internal listeners (NetBird,
# h2c, the shared peer credential read from the local TerraBase container into a private header file, never printed
# or passed on a command line), it creates a fresh user's Starter organization and project. It then creates one name in
# gru and fra at the same moment until both regions accept it (up to ATTEMPTS names), and expects in every region:
# both databases listed, the smaller id under the name, the larger under <name>-conflict-<its id's last 6, lowercase>
# with previousName and conflictOf, and one database.name_conflict audit event for the loser. It deletes the
# databases, the project and the organization afterwards.
# Exits 0 when the conflict kept both databases, 1 otherwise.
set -euo pipefail
ATTEMPTS=${ATTEMPTS:-10}
PORT=8081
exec 9> "$HOME/.name-conflict-proof.lock"
flock -n 9 || { echo "another proof run holds $HOME/.name-conflict-proof.lock" >&2; exit 1; }
umask 077
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

# The running TerraBase task: its container's image is ghcr.io/terrascale/terrabase.
container=$(join <(sudo -n ctr -n terrascale containers ls | awk '$2 ~ /terrascale\/terrabase@/ {print $1}' | sort) \
  <(sudo -n ctr -n terrascale tasks ls | awk '$3 == "RUNNING" {print $1}' | sort) | head -1)
pid=$(sudo -n ctr -n terrascale tasks ls | awk -v c="$container" '$1 == c {print $2}')
secret_file=$(sudo -n cat "/proc/$pid/environ" | tr '\0' '\n' | sed -n 's/^TERRABASE_PEER_SECRET_FILE=//p')
# printf is a builtin: the credential reaches only the header file, never a process's arguments.
printf 'X-TerraBase-Peer-Credential: %s\n' "$(sudo -n cat "/proc/$pid/root$secret_file")" > "$work/headers"
# One "<node> <region> <netbird-ipv4>" line per region.
mapfile -t peers < <(sudo -n cat "/proc/$pid/root/run/ts-orch/peers/peers")
declare -A node
for line in "${peers[@]}"; do read -r _ region ip <<< "$line"; node[$region]=$ip; done
regions=(gru sjc fra)
for region in "${regions[@]}"; do [[ -n ${node[$region]:-} ]] || { echo "no $region node in the peers file" >&2; exit 1; }; done

ulid() { python3 -c 'import os,time
a="0123456789ABCDEFGHJKMNPQRSTVWXYZ"; n=(int(time.time()*1000)<<80)|int.from_bytes(os.urandom(10),"big")
print("".join(a[(n>>(5*i))&31] for i in range(25,-1,-1)))'; }
user=$(ulid)
echo "X-TerraBase-User-Id: $user" >> "$work/headers"

# call REGION METHOD PATH [JSON] -> prints "<status> <body>"
call() {
  local out="$work/body.$BASHPID.$RANDOM" status
  local args=(-s --http2-prior-knowledge -m 120 -o "$out" -w '%{http_code}' -X "$2" -H "@$work/headers")
  [[ $# -ge 4 ]] && args+=(-H 'Content-Type: application/json' -d "$4")
  status=$(curl "${args[@]}" "http://${node[$1]}:$PORT$3")
  echo "$status $(cat "$out")"; rm -f -- "$out"
}
field() { python3 -c "import json,sys; print(json.loads(sys.argv[1])$2)" "$1"; }

read -r status body <<< "$(call gru POST /v1/management/organizations '{"name":"ts-1120 name conflict proof"}')"
[[ $status == 201 ]] || { echo "create organization: $status $body" >&2; exit 1; }
organization=$(field "$body" '["id"]')
read -r status body <<< "$(call gru PUT "/v1/management/organizations/$organization/plan" '{"plan":"starter"}')"
[[ $status == 200 ]] || { echo "set plan: $status $body" >&2; exit 1; }
read -r status body <<< "$(call gru POST "/v1/management/organizations/$organization/projects" '{"name":"proof"}')"
[[ $status == 201 ]] || { echo "create project: $status $body" >&2; exit 1; }
project=$(field "$body" '["id"]')
databases="/v1/management/organizations/$organization/projects/$project/databases"
echo "user $user organization $organization project $project"

# Every region sees the Starter organization and its project before the race.
for _ in $(seq 60); do
  ready=1
  for region in "${regions[@]}"; do
    read -r status body <<< "$(call "$region" GET "/v1/management/organizations/$organization")"
    [[ $status == 200 && $(field "$body" '["plan"]') == starter ]] || ready=0
    read -r status body <<< "$(call "$region" GET "$databases")"
    [[ $status == 200 ]] || ready=0
  done
  (( ready )) && break; sleep 1
done
(( ready )) || { echo "the organization did not reach every region" >&2; exit 1; }

# Every database a create answered 201 for, as "<id> <home>", so the cleanup deletes each one.
: > "$work/created"
failed=0 name='' pair=''
for attempt in $(seq "$ATTEMPTS"); do
  candidate="twin$attempt"
  call gru POST "$databases" "{\"name\":\"$candidate\"}" > "$work/race.gru" &
  call fra POST "$databases" "{\"name\":\"$candidate\"}" > "$work/race.fra" &
  wait
  accepted=0
  for region in gru fra; do
    read -r status body < "$work/race.$region"
    echo "attempt $attempt $candidate $region: $status"
    if [[ $status == 201 ]]; then
      accepted=$((accepted + 1)); echo "$(field "$body" '["id"]') $region" >> "$work/created"
    fi
  done
  if (( accepted == 2 )); then name=$candidate; pair=$(tail -n 2 "$work/created" | awk '{print $1}' | sort | tr '\n' ' '); break; fi
done

if [[ -z $name ]]; then
  echo "no name was accepted by both regions in $ATTEMPTS attempts"; failed=1
else
  read -r winner loser <<< "$pair"
  conflict="$name-conflict-$(tr '[:upper:]' '[:lower:]' <<< "${loser: -6}")"
  echo "race $name: winner $winner, loser $loser, expected conflict name $conflict"
  # Both live in every region under unique names, the loser marked with the name it lost and the database that kept it.
  check() {
    python3 -c 'import json,sys
items={d["id"]:d for d in json.loads(sys.argv[1])["items"]}
w,l,name,conflict=sys.argv[2:6]
ok=w in items and l in items and items[w]["name"]==name and items[l]["name"]==conflict \
  and items[l].get("previousName")==name and items[l].get("conflictOf")==w and items[w].get("previousName") is None
print("yes" if ok else "no")' "$1" "$winner" "$loser" "$name" "$conflict"
  }
  for region in "${regions[@]}"; do
    kept=no
    for _ in $(seq 120); do
      read -r status body <<< "$(call "$region" GET "$databases")"
      [[ $status == 200 && $(check "$body") == yes ]] && { kept=yes; break; }
      sleep 1
    done
    echo "$region: both databases kept, winner named $name, loser named $conflict: $kept"
    [[ $kept == yes ]] || { echo "$region listing: $status $body"; failed=1; }
  done
  # The loser's home records the event; it replicates like any catalog write.
  for _ in $(seq 60); do
    read -r status body <<< "$(call gru GET "/v1/management/organizations/$organization/audit-events?limit=100")"
    events=$(python3 -c 'import json,sys
print(sum(1 for e in json.loads(sys.argv[1])["items"] if e["action"]=="database.name_conflict" and e.get("target")==sys.argv[2]))' "$body" "$loser" 2>/dev/null || echo x)
    [[ $events == 1 ]] && break; sleep 1
  done
  echo "database.name_conflict audit events for the loser: $events"
  [[ $events == 1 ]] || { echo "audit: $status $body"; failed=1; }
fi

while read -r id home; do
  read -r status body <<< "$(call "$home" DELETE "$databases/$id")"
  echo "delete database $id in $home: $status"
  [[ $status == 204 ]] || failed=1
done < "$work/created"
for _ in $(seq 60); do
  read -r status body <<< "$(call gru GET "$databases")"
  [[ $status == 200 && $(field "$body" '["items"].__len__()') == 0 ]] && break; sleep 1
done
read -r status body <<< "$(call gru DELETE "/v1/management/organizations/$organization/projects/$project")"
echo "delete project: $status"
read -r status body <<< "$(call gru DELETE "/v1/management/organizations/$organization")"
echo "delete organization: $status"
echo "result: $([[ $failed == 0 ]] && echo PASS || echo FAIL)"
exit "$failed"
