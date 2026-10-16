#!/usr/bin/env bash
# ts-vtest-tb: the disposable V-wave cluster (terrascale-previews-515; gru, sjc, fra; shared-cpu-2x 512 MB like prod).
#   vtest.sh create <sha>      app, Tigris bucket, generated secrets, image at <sha>, one machine + volume per region
#   vtest.sh clone            clone gru's machine to sjc and fra, each on a new volume (part of create)
#   vtest.sh deploy <sha>      publish terrabase at <sha> here (run it on a ts-heavy host), build the faults image on Fly's
#                              builder and roll it to every machine
#   vtest.sh bootstrap         synthetic owner/org/project + database "e2e" (gru home, sjc + fra replicas) -> tenant.env
#   vtest.sh status            per machine: pinned /healthz identity and /readyz
#   vtest.sh partition <r>     drop every 6PN packet between region <r> and the other two (both ends);  heal: undo all
#   vtest.sh with-key <cmd..>  run <cmd> with the pinned-run target (env) and a fresh 24 h documents:read/write key in its
#                              environment only, revoked on exit
#   vtest.sh env               print the TERRABASE_E2E_FLY_* exports (no secret) for a pinned run
#   vtest.sh rotate-peer       stage a fresh peer credential (applied by the next deploy) and keep it, 0600, in
#                              $peer, so operator calls pass #271's peer gate
#   vtest.sh destroy           delete the bucket and the app (machines + volumes); the owner runs this after the reruns
# Every created resource is appended to ../resources.md. Secrets are generated into a 0600 temp file, piped to
# `fly secrets import`, and deleted: never printed, never in argv, git or logs.
set -euo pipefail; set +x
here=$(cd "$(dirname "$0")" && pwd); repo=$(git -C "$here" rev-parse --show-toplevel)
A=ts-vtest-tb O=terrascale-previews-515 REGIONS=(gru sjc fra) URL=https://ts-vtest-tb.fly.dev
ledger=$here/../resources.md state=$here/../state.env
# The peer credential as a curl header file (curl -H @file): outside the repo, never in argv, deleted by destroy.
peer=${XDG_RUNTIME_DIR:-/tmp}/ts-vtest-peer.header
log() { echo "$(date -u +%FT%TZ) $*" | tee -a "$ledger" >&2; }
fly() { flyctl "$@"; }
machines_json() { fly machine list -a $A --json; }
machine_of() { machines_json | jq -r --arg r "$1" '[.[] | select(.region == $r)] | if length == 1 then .[0].id else error("want one machine in \($r), found \(length)") end'; }
ip_of() { machines_json | jq -r --arg r "$1" '.[] | select(.region == $r) | .private_ip'; }
save_state() {
  local G S F; G=$(machine_of gru) S=$(machine_of sjc) F=$(machine_of fra)
  { echo "G=$G S=$S F=$F"; [[ -f $state ]] && grep -E '^(owner|org|project)=' "$state" || true; } > "$state.new"; mv "$state.new" "$state"
}
# Operator calls on a machine's internal listener (h2c) through flyctl proxy; after #271 they send the peer credential
# from $peer (create and rotate-peer write it; a cluster created before either needs rotate-peer + deploy).
op_proxy() { fly proxy "$1:8081" "$2.vm.$A.internal" -a $A > /dev/null 2>&1 & echo $!; }
wait_port() { for _ in $(seq 1 30); do curl -s -m 2 -o /dev/null --http2-prior-knowledge "http://127.0.0.1:$1/healthz" && return; sleep 1; done; echo "proxy :$1 not up" >&2; return 1; }

secrets() {
  local tmp p; tmp=$(mktemp); chmod 600 "$tmp"; trap 'rm -f "$tmp"' RETURN; p=$(openssl rand -hex 32)
  (umask 077; printf 'X-TerraBase-Peer-Credential: %s\n' "$p" > "$peer")
  { [[ ${1:-} == peer ]] || printf 'TERRABASE_MASTER_KEY=%s\n' "$(openssl rand -base64 32)"; printf 'TERRABASE_PEER_SECRET=%s\n' "$p"; } > "$tmp"
  fly secrets import --stage -a $A < "$tmp" > /dev/null
  log "secrets staged on $A: $(cut -d= -f1 "$tmp" | paste -sd' ') (generated, temp file deleted; peer header kept 0600 outside the repo)"
}

deploy() {
  local sha; sha=$(git -C "$repo" rev-parse --verify "$1^{commit}"); local src=/tmp/vtest-src-$sha
  [[ -d $src ]] || { git -C "$repo" fetch -q origin "$sha" 2> /dev/null; git -C "$repo" worktree add --detach "$src" "$sha" > /dev/null; }
  local ctx=/tmp/vtest-ctx-$sha; rm -rf "$ctx"
  # The repo Dockerfile's publish-terrabase step, run here (a ts-heavy host): CI=true restores in locked mode.
  CI=true TERRABASE_REVISION=$sha nice -n 19 dotnet publish "$src/src/TerraBase" --configuration Release --runtime linux-x64 --self-contained --output "$ctx/publish" > "$ctx.publish.log" 2>&1 \
    || { tail -20 "$ctx.publish.log"; return 1; }
  fly deploy "$ctx" -a $A --config "$here/fly.vtest.toml" --dockerfile "$here/Containerfile.faults" --remote-only \
    --image-label "vtest-${sha:0:12}" --ha=false --wait-timeout 10m --yes
  local image; image=$(machines_json | jq -r '[.[].config.image] | unique | join(",")')
  log "deployed $sha to $A: image $image digest $(machines_json | jq -r '[.[].image_ref.digest] | unique | join(",")')"
}

case ${1:?usage: vtest.sh create|clone|deploy|bootstrap|status|partition|heal|with-key|env|rotate-peer|destroy} in
create)
  sha=${2:?create <sha>}
  fly apps create $A --org $O > /dev/null; log "created app $A in $O"
  # Tigris: TerraBase refuses to start outside Development without a backup bucket (Program.cs). Credentials go straight to the app.
  fly storage create -a $A -o $O -n $A -y > /dev/null; log "created Tigris bucket $A (credentials set as $A secrets by flyctl)"
  secrets
  deploy "$sha"
  "$0" clone ;;
clone)
  vol() { fly volumes list -a $A --json | jq -r --arg m "$1" '.[] | select(.attached_machine_id == $m) | .id'; }
  G=$(machine_of gru); log "created machine $G in gru + volume $(vol "$G")"
  for r in sjc fra; do
    fly machine clone "$G" -a $A --region $r > /dev/null
    m=$(machine_of $r); log "created machine $m in $r + volume $(vol "$m")"
  done
  save_state ;;
deploy) deploy "${2:?deploy <sha>}"; save_state 2> /dev/null || echo "fewer than three machines: run vtest.sh clone" >&2 ;;
bootstrap)
  save_state; source "$state"; pid=$(op_proxy 18181 "$G"); trap 'kill $pid 2> /dev/null' EXIT; wait_port 18181
  owner=$(python3 -I -c 'import os,time; e="0123456789ABCDEFGHJKMNPQRSTVWXYZ"; n=(int(time.time()*1000)<<80)|int.from_bytes(os.urandom(10)); print("".join(e[(n>>(5*i))&31] for i in range(25,-1,-1)))')
  mg() { curl -sS --fail-with-body --http2-prior-knowledge -H @"$peer" -H "X-TerraBase-User-Id: $owner" -H 'content-type: application/json' "$@"; }
  org=$(mg -d '{"name":"vtest"}' http://127.0.0.1:18181/v1/management/organizations | jq -r .id)
  project=$(mg -d '{"name":"vtest"}' "http://127.0.0.1:18181/v1/management/organizations/$org/projects" | jq -r .id)
  mg -d '{"name":"e2e","homeRegion":"gru","replicaRegions":["sjc","fra"]}' "http://127.0.0.1:18181/v1/management/organizations/$org/projects/$project/databases" | jq -c '{id,name,homeRegion,replicaRegions}'
  printf 'owner=%s\norg=%s\nproject=%s\n' "$owner" "$org" "$project" >> "$state"; log "bootstrapped synthetic tenant org $org project $project database e2e" ;;
status)
  source "$state"
  for m in $G $S $F; do
    echo "$m $(curl -sS -m 10 -H "fly-force-instance-id: $m" $URL/healthz | jq -c '{region,machine,revision}') readyz=$(curl -sS -m 10 -o /dev/null -w '%{http_code}' -H "fly-force-instance-id: $m" $URL/readyz)"
  done ;;
partition|heal)
  source "$state"
  rule() { fly ssh console -a $A --machine "$1" -C "sh -c '$2'" > /dev/null; }
  if [[ $1 == heal ]]; then for m in $G $S $F; do rule "$m" 'ip6tables -F INPUT && ip6tables -F OUTPUT'; done; log "healed: 6PN rules flushed on all three"; exit; fi
  cut=${2:?partition <region>}; mc=$(machine_of "$cut"); ic=$(ip_of "$cut")
  for r in "${REGIONS[@]}"; do
    [[ $r == "$cut" ]] && continue; m=$(machine_of "$r"); i=$(ip_of "$r")
    rule "$mc" "ip6tables -A INPUT -s $i -j DROP && ip6tables -A OUTPUT -d $i -j DROP"
    rule "$m" "ip6tables -A INPUT -s $ic -j DROP && ip6tables -A OUTPUT -d $ic -j DROP"
  done
  log "partitioned $cut from the other two (6PN drops both ends)" ;;
with-key)
  shift; source "$state"; pid=$(op_proxy 18181 "$G"); trap 'kill $pid 2> /dev/null' EXIT; wait_port 18181
  keys=http://127.0.0.1:18181/v1/management/organizations/$org/projects/$project/api-keys; name="run-$(date -u +%Y%m%dT%H%M%S)"
  issued=$(curl -sS --fail --http2-prior-knowledge -H @"$peer" -H 'content-type: application/json' -d "$(jq -cn --arg n "$name" --arg o "$org" --arg p "$project" \
    '{name: $n, scopes: ["documents:read", "documents:write"], organizationId: $o, projectId: $p, expiresAt: (now + 86400 | todate)}')" http://127.0.0.1:18181/admin/api-keys)
  id=$(jq -r .id <<< "$issued"); echo "key $name id $id" >&2
  revoke() { curl -sS --fail --http2-prior-knowledge -H @"$peer" -H "X-TerraBase-User-Id: $owner" -X DELETE -o /dev/null "$keys/$id" && echo "key $id revoked" >&2 || echo "REVOKE FAILED for key $id" >&2; kill $pid 2> /dev/null; }
  trap revoke EXIT
  TERRABASE_E2E_FLY_API_KEY=$(jq -r .secret <<< "$issued"); unset issued; export TERRABASE_E2E_FLY_API_KEY
  eval "$("$0" env)"
  "$@" ;;
env)
  source "$state"
  echo "export TERRABASE_E2E_FLY_URL=$URL/ TERRABASE_E2E_FLY_APP=$A TERRABASE_E2E_FLY_ORG=$O TERRABASE_E2E_FLY_MACHINES=gru=$G,sjc=$S,fra=$F"
  echo "export TERRABASE_E2E_FLY_PROJECT=$project TERRABASE_E2E_FLY_DATABASE=e2e TERRABASE_E2E_FLY_REVISION=$(curl -sS -H "fly-force-instance-id: $G" $URL/healthz | jq -r .revision)" ;;
rotate-peer) secrets peer; echo "staged: run vtest.sh deploy <sha> to apply it" >&2 ;;
destroy)
  fly storage destroy $A -y > /dev/null && log "destroyed Tigris bucket $A"
  fly apps destroy $A -y > /dev/null && log "destroyed app $A (its machines and volumes)"; rm -f "$peer" ;;
*) echo "unknown command $1" >&2; exit 2 ;;
esac
