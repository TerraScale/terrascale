#!/usr/bin/env bash
# Production proof of the strict plan database allowance (decision 0016, TS-995).
#
# Run on a ts-orch host (ssh ts-gru-1 'bash -s' < scripts/terrabase/quota-escrow-proof.sh). As the platform operator on
# the internal listeners (NetBird, h2c, the shared peer credential read from the local TerraBase container, never
# printed), it creates a dedicated user's Starter organization and project. Each of ROUNDS rounds sends six concurrent
# creates (two to each region's node) and expects exactly five 201 and one 409 database_limit; then, with four databases
# listed in every region, one create to each region at once, and expects exactly one 201. It deletes the databases,
# the project and the organization afterwards.
# Exits 0 when every round held, 1 otherwise.
set -euo pipefail
ROUNDS=${ROUNDS:-3}
PORT=8081

# The running TerraBase task: its container's image is ghcr.io/terrascale/terrabase.
container=$(join <(sudo -n ctr -n terrascale containers ls | awk '$2 ~ /terrascale\/terrabase@/ {print $1}' | sort) \
  <(sudo -n ctr -n terrascale tasks ls | awk '$3 == "RUNNING" {print $1}' | sort) | head -1)
pid=$(sudo -n ctr -n terrascale tasks ls | awk -v c="$container" '$1 == c {print $2}')
secret_file=$(sudo -n cat "/proc/$pid/environ" | tr '\0' '\n' | sed -n 's/^TERRABASE_PEER_SECRET_FILE=//p')
secret=$(sudo -n cat "/proc/$pid/root$secret_file")
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
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

# call REGION METHOD PATH [JSON] -> prints "<status> <body>"
call() {
  local out="$work/body.$BASHPID.$RANDOM" status
  local args=(-s --http2-prior-knowledge -m 30 -o "$out" -w '%{http_code}' -X "$2"
    -H "X-TerraBase-Peer-Credential: $secret" -H "X-TerraBase-User-Id: $user")
  [[ $# -ge 4 ]] && args+=(-H 'Content-Type: application/json' -d "$4")
  status=$(curl "${args[@]}" "http://${node[$1]}:$PORT$3")
  echo "$status $(cat "$out")"; rm -f -- "$out"
}
field() { python3 -c "import json,sys; print(json.loads(sys.argv[1])$2)" "$1"; }

read -r status body <<< "$(call gru POST /v1/management/organizations '{"name":"ts-995 escrow proof"}')"
[[ $status == 201 ]] || { echo "create organization: $status $body" >&2; exit 1; }
organization=$(field "$body" '["id"]')
read -r status body <<< "$(call gru PUT "/v1/management/organizations/$organization/plan" '{"plan":"starter"}')"
[[ $status == 200 ]] || { echo "set plan: $status $body" >&2; exit 1; }
read -r status body <<< "$(call gru POST "/v1/management/organizations/$organization/projects" '{"name":"proof"}')"
[[ $status == 201 ]] || { echo "create project: $status $body" >&2; exit 1; }
project=$(field "$body" '["id"]')
databases="/v1/management/organizations/$organization/projects/$project/databases"
echo "user $user organization $organization project $project"

listed() { local status body; read -r status body <<< "$(call "$1" GET "$databases")"; [[ $status == 200 ]] && field "$body" '["items"].__len__()'; }
until_listed() {
  for _ in $(seq 60); do
    local all=1
    for region in "${regions[@]}"; do [[ $(listed "$region" || echo x) == "$1" ]] || all=0; done
    (( all )) && return 0; sleep 1
  done
  return 1
}

# Every region sees the Starter organization and its project before the first round.
for _ in $(seq 60); do
  ready=1
  for region in "${regions[@]}"; do
    read -r status body <<< "$(call "$region" GET "/v1/management/organizations/$organization")"
    [[ $status == 200 && $(field "$body" '["plan"]') == starter && $(listed "$region" || echo x) == 0 ]] || ready=0
  done
  (( ready )) && break; sleep 1
done
(( ready )) || { echo "the organization did not reach every region" >&2; exit 1; }

# burst NAME REGION... : one concurrent create per named region; records "<id> <home>" or "409 <body>" lines in $work/NAME.
burst() {
  local name=$1 index=0 region status body; shift
  for region in "$@"; do
    index=$((index + 1)) sequence=$((sequence + 1))
    call "$region" POST "$databases" "{\"name\":\"$name-$region-$sequence\"}" > "$work/create.$name.$index" &
  done
  wait
  created=0 refused=0
  for index in $(seq "$#"); do
    read -r status body < "$work/create.$name.$index"
    case $status in
      201) created=$((created + 1)); field "$body" '["id"], end=" "'; field "$body" '["homeRegion"]' ;;
      409) [[ $(field "$body" '["error"]["code"]') == database_limit ]] && refused=$((refused + 1)); echo "409 $body" ;;
      *) echo "unexpected $status $body" ;;
    esac
  done >> "$work/$name"
}

# Deletes every database recorded in $work/NAME at its home region, then waits until no region lists any.
clear_round() {
  local id home status body
  while read -r id home; do
    [[ $id == 409 || $id == unexpected ]] && continue
    read -r status body <<< "$(call "$home" DELETE "$databases/$id")"
    [[ $status == 204 ]] || { echo "delete $id in $home: $status $body"; failed=1; }
  done < "$work/$1"
  until_listed 0 || { echo "deletes did not reach every region"; failed=1; }
}

failed=0 sequence=0
for round in $(seq "$ROUNDS"); do
  # Six concurrent creates from empty, two per region: exactly five.
  : > "$work/a$round"
  burst "a$round" "${regions[@]}" "${regions[@]}"
  until_listed 5 && listed_ok=yes || listed_ok=no
  echo "round $round six: $created created, $refused refused with database_limit, five listed in every region: $listed_ok"
  cat "$work/a$round"
  [[ $created == 5 && $refused == 1 && $listed_ok == yes ]] || failed=1
  clear_round "a$round"

  # Four replicated everywhere, then one create per region at once: each region sees four locally, so only escrow
  # keeps this to exactly one.
  : > "$work/b$round"
  for fill in 1 2 3 4; do burst "b$round" "${regions[$(( (fill + round) % 3 ))]}"; done
  until_listed 4 || { echo "the four did not reach every region"; failed=1; }
  burst "b$round" "${regions[@]}"
  until_listed 5 && listed_ok=yes || listed_ok=no
  echo "round $round edge: $created of 3 created, $refused refused with database_limit, five listed in every region: $listed_ok"
  tail -n 3 "$work/b$round"
  [[ $created == 1 && $refused == 2 && $listed_ok == yes ]] || failed=1
  clear_round "b$round"
done

read -r status body <<< "$(call gru DELETE "/v1/management/organizations/$organization/projects/$project")"
echo "delete project: $status"
read -r status body <<< "$(call gru DELETE "/v1/management/organizations/$organization")"
echo "delete organization: $status"
unset secret
echo "result: $([[ $failed == 0 ]] && echo PASS || echo FAIL)"
exit "$failed"
