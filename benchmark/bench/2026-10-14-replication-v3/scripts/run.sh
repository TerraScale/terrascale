#!/usr/bin/env bash
# V3 live load against the isolated cluster ts-vtest-tb (org terrascale-previews-515). Usage: run.sh <shape-bench binary> <out dir>
# Creates a test tenant (organization, project, database) through gru's operator API (flyctl proxy to 8081), mints one
# documents key narrowed to that database straight into the ts-vtest-load Fly secret (never a file, argv or log),
# runs one load machine per region (shard 0 in gru), merges the shards on the gru machine, then deletes the database,
# project and organization, revokes the key and destroys every load machine. ts-vtest-tb itself is never changed.
set -euo pipefail; set +x
bin=$1; out=$2; mkdir -p "$out"
tb=ts-vtest-tb load=ts-vtest-load org=terrascale-previews-515 port=18191 base=http://127.0.0.1:18191
regions=(gru sjc fra); steps=${STEPS:-1,100,1000,10000}
here=$(cd "$(dirname "$0")" && pwd); work=$(mktemp -d)
log() { echo "$(date -u +%FT%TZ) $*" | tee -a "$out/ledger.log" >&2; }
ulid() { local a=0123456789ABCDEFGHJKMNPQRSTVWXYZ ms o="" b; ms=$(date +%s%3N); for _ in {1..10}; do o=${a:ms%32:1}$o; ms=$((ms / 32)); done
  for b in $(od -An -N16 -tu1 /dev/urandom); do o+=${a:b%32:1}; done; echo "$o"; }
internal() { local m=$1 p=$2; shift 2; curl -sS --fail-with-body --http2-prior-knowledge -X "$m" -H 'content-type: application/json' -H "X-TerraBase-User-Id: $owner" "$base$p" "$@"; }
commit() { internal POST /v1/management/commits -d "$(jq -cn --arg id "$(ulid)" --argjson ops "$1" '{id: $id, ops: $ops}')"; }

owner=$(ulid) organization=$(ulid) project=$(ulid) database=$(ulid) key_id="" proxy=""
cleanup() {
  local status=$? id
  for id in $(flyctl machine list -a "$load" --json 2> /dev/null | jq -r '.[].id'); do
    flyctl machine destroy "$id" -a "$load" --force > /dev/null 2>&1 && log "destroyed load machine $id" || log "WARNING: destroy $id of $load by hand"
  done
  if [[ -n "$key_id" ]]; then
    internal DELETE "/v1/management/organizations/$organization/projects/$project/api-keys/$key_id" > /dev/null && log "revoked key $key_id" || log "WARNING: revoke key $key_id by hand"
  fi
  local ids=(--arg o "$organization" --arg p "$project" --arg d "$database") ops
  # A replicated database is deleted only once its replica regions are removed.
  for ops in '[{op: "set_database_regions", organization: $o, project: $p, database: $d, replica_regions: []}]' \
    '[{op: "delete_database", organization: $o, project: $p, database: $d}]' '[{op: "delete_project", organization: $o, project: $p}]' \
    '[{op: "delete_organization", organization: $o}]'; do
    commit "$(jq -cn "${ids[@]}" "$ops")" > "$work/commit.out" 2>&1 && log "commit ok: $(jq -cn "${ids[@]}" "$ops" | jq -c '.[0].op')" \
      || log "WARNING: $(jq -cn "${ids[@]}" "$ops" | jq -c '.[0].op') failed: $(head -c 300 "$work/commit.out")"
  done
  [[ -n "$proxy" ]] && kill "$proxy" 2> /dev/null
  rm -rf "$work"; exit "$status"
}

gru=$(flyctl machine list -a "$tb" --json | jq -r '[.[] | select(.region == "gru" and .state == "started")][0].id')
flyctl proxy "$port:8081" "$gru.vm.$tb.internal" -a "$tb" > /dev/null 2>&1 & proxy=$!
for _ in $(seq 30); do curl -s -o /dev/null --http2-prior-knowledge "$base/" && break; sleep 1; done
trap cleanup EXIT
commit "$(jq -cn --arg o "$organization" --arg p "$project" --arg d "$database" '[{op: "create_organization", id: $o, name: "V3 shape load"},
  {op: "set_plan", organization: $o, plan: "enterprise"}, {op: "create_project", organization: $o, id: $p, name: "v3"},
  {op: "create_database", organization: $o, project: $p, name: "v3", id: $d, home_region: "gru", replica_regions: ["sjc", "fra"]}]')" > /dev/null
log "created organization $organization (owner user $owner, enterprise plan), project $project, database $database (gru + sjc, fra) on $tb"
flyctl apps list -o "$org" --json | jq -e --arg a "$load" 'any(.[]; .Name == $a)' > /dev/null || { flyctl apps create "$load" -o "$org" > /dev/null; log "created app $load"; }
# One pipe: the id goes to a variable, the secret straight into Fly.
key_id=$(internal POST /admin/api-keys -d "$(jq -cn --arg o "$organization" --arg p "$project" --arg d "$database" --arg e "$(date -u -d '+6 hours' +%FT%TZ)" \
  '{name: "v3-load", scopes: ["documents:read", "documents:write"], organizationId: $o, projectId: $p, databaseId: $d, expiresAt: $e}')" \
  | jq -jer '"\(.id)\nTS_BENCH_API_KEY=\(.secret)\n"' | { read -r id; echo "$id"; flyctl secrets import -a "$load" --stage > /dev/null || echo "import failed" >&2; })
[[ -n "$key_id" ]] || { log "key mint failed"; exit 1; }
flyctl secrets list -a "$load" --json | jq -e 'any(.[]; (.name // .Name) == "TS_BENCH_API_KEY")' > /dev/null || { log "secret import failed"; exit 1; }
log "key $key_id (documents read/write on database $database, 6 h) set as $load secret TS_BENCH_API_KEY"

cp "$bin" "$work/shape-bench"; printf 'FROM alpine:3.22\nCOPY shape-bench /shape-bench\n' > "$work/Dockerfile"; printf 'app = "%s"\n' "$load" > "$work/fly.toml"
label="v3-$(date -u +%Y%m%d%H%M%S)"
(cd "$work" && flyctl deploy -a "$load" -c fly.toml --build-only --push --image-label "$label" --remote-only > "$out/image.log" 2>&1)
log "image registry.fly.io/$load:$label $(grep -o 'sha256:[0-9a-f]*' "$out/image.log" | tail -1)"

start=$(( $(date +%s) + 240 )); collection="v3-$(ulid | tr A-Z a-z)"
declare -A machine
for i in "${!regions[@]}"; do
  r=${regions[$i]}
  cmd="/shape-bench load -app $tb -project $project -database v3 -collection $collection -start $start -shard $i -shards ${#regions[@]} -steps $steps -out /work/shard.json > /work/load.log 2>&1; echo \$? > /work/exit; sleep infinity"
  machine[$r]=$(flyctl machine run "registry.fly.io/$load:$label" -a "$load" -r "$r" --vm-size shared-cpu-2x --vm-memory 2048 --name "v3-load-$r" \
    --restart no --autostop=off --detach -- sh -c "mkdir -p /work; $cmd" | sed -n 's/.*Machine ID: \([0-9a-f]*\).*/\1/p')
  log "load machine ${machine[$r]} in $r (shard $i, shared-cpu-2x 2048 MB)"
done
log "start $(date -u -d @"$start" +%FT%TZ), steps $steps, collection $collection"
for r in "${regions[@]}"; do
  until flyctl ssh console -a "$load" --machine "${machine[$r]}" -C "test -f /work/exit" > /dev/null 2>&1; do sleep 20; done
  flyctl ssh sftp get -a "$load" --machine "${machine[$r]}" /work/shard.json "$out/shard-$r.json" > /dev/null || log "no shard record from $r"
  flyctl ssh sftp get -a "$load" --machine "${machine[$r]}" /work/load.log "$out/load-$r.log" > /dev/null || true
  log "$r exit $(flyctl ssh console -a "$load" --machine "${machine[$r]}" -C "cat /work/exit" 2> /dev/null | tr -d '\r')"
done
for r in sjc fra; do flyctl ssh sftp put -a "$load" --machine "${machine[gru]}" "$out/shard-$r.json" "/work/shard-$r.json" > /dev/null; done
flyctl ssh console -a "$load" --machine "${machine[gru]}" -C "sh -c '/shape-bench merge -out /work/v3 /work/shard.json /work/shard-sjc.json /work/shard-fra.json > /work/merge.log 2>&1; echo merge exit \$? >> /work/merge.log'" > /dev/null || true
for f in v3.md v3.json merge.log; do flyctl ssh sftp get -a "$load" --machine "${machine[gru]}" "/work/$f" "$out/$f" > /dev/null || log "no $f"; done
log "finished; $(tail -1 "$out/merge.log")"
grep -q "^merge exit 0$" "$out/merge.log"  # the run fails when merge found the bound broken or a shard error
