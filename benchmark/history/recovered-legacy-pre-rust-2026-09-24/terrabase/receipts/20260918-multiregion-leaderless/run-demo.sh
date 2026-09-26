#!/usr/bin/env bash
# Three real TerraBase member processes in three containers ("regions"),
# launched from environment configuration alone, with 40ms ± 5ms netem delay
# applied ONLY to member-to-member traffic (the prio band filter matches the
# other members' bridge IPs). The driver on the host is undelayed, so its
# timings measure the members, not itself.
#
# Topology: relay ring gru -> fra -> nak -> gru. A write at gru reaches fra
# after one 40ms hop and nak after two (fra's relay of the durably applied
# push).
set -euo pipefail

DEMO_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="${PUBLISH_DIR:-/tmp/multiregion-publish}"
CERTS="$DEMO_DIR/certs"
RESULTS="$DEMO_DIR/results"
NETWORK=terrabase-demo
IMAGE=terrabase-member:demo
DELAY_MS="${DELAY_MS:-40}"
SAMPLES="${SAMPLES:-15}"

cd "$DEMO_DIR"
mkdir -p "$CERTS" "$RESULTS"

echo "== build driver =="
dotnet build TerraBaseMultiRegionDemo.csproj -c Release --nologo -m:1 \
  /p:BuildInParallel=false /p:UseSharedCompilation=false

if [ ! -d "$PUBLISH_DIR" ]; then
  echo "publish the member host first:"
  echo "  dotnet publish terrabase/.worktrees/anna/src/TerraBase/TerraBase.csproj -c Release -o $PUBLISH_DIR"
  exit 1
fi

echo "== certificates + manifest =="
rm -rf "$CERTS"
DRIVER="bin/Release/net10.0/TerraBaseMultiRegionDemo"
"$DRIVER" certgen "$CERTS"
DATABASE="$(python3 -c "import json;print(json.load(open('$CERTS/manifest.json'))['database'])")"
TENANT="$(python3 -c "import json;print(json.load(open('$CERTS/manifest.json'))['tenant'])")"
EPOCH=9

echo "== container image =="
podman build -t "$IMAGE" -f Containerfile .

echo "== network + members =="
podman network rm -f "$NETWORK" >/dev/null 2>&1 || true
podman network create "$NETWORK" >/dev/null

member() { # node region port next
  local node="$1" region="$2" port="$3" next="$4"
  local allowlist
  # One stable HMAC key per member container: pull continuations keep
  # validating across that member's restarts (env is fixed at creation).
  local CHECKPOINT_KEY
  CHECKPOINT_KEY="$(openssl rand -hex 32)"
  allowlist="$(python3 - "$CERTS/manifest.json" "$node" "$next" <<'PY'
import json, sys
manifest = json.load(open(sys.argv[1]))
node, next_node = sys.argv[2], sys.argv[3]
entries = []
for member in manifest["members"]:
    entry = {
        "fingerprint": member["fingerprint"],
        "memberId": member["node"],
        "epoch": manifest["epoch"],
        "databases": [manifest["database"]],
        "tenants": [manifest["tenant"]],
    }
    if member["node"] == next_node:
        entry["url"] = f"https://{next_node}:8443"
    entries.append(entry)
entries.append({
    "fingerprint": manifest["observerFingerprint"],
    "memberId": "observer",
    "epoch": manifest["epoch"],
    "databases": [manifest["database"]],
    "tenants": [manifest["tenant"]],
})
print(json.dumps(entries, separators=(",", ":")))
PY
)"
  podman rm -f "$node" >/dev/null 2>&1 || true
  podman run -d --name "$node" --network "$NETWORK" --cap-add NET_ADMIN \
    -p "127.0.0.1:$port:8443" \
    -v "$PUBLISH_DIR":/app:ro \
    -v "$CERTS":/certs:ro \
    -v "/$node-data" \
    -e TERRABASE_MEMBER_ENABLED=true \
    -e TERRABASE_PEER_URL=https://0.0.0.0:8443 \
    -e TERRABASE_NODE_ID="$node" \
    -e TERRABASE_REPLICA_EPOCH="$EPOCH" \
    -e TERRABASE_MEMBER_CERT_FILE=/certs/"$node".crt \
    -e TERRABASE_MEMBER_KEY_FILE=/certs/"$node".key \
    -e TERRABASE_CLUSTER_CA_FILE=/certs/ca.crt \
    -e TERRABASE_PEER_ALLOWLIST="$allowlist" \
    -e TERRABASE_OBSERVABILITY_URL=http://127.0.0.1:8420 \
    -e TERRABASE_REPLICATION_CHECKPOINT_KEY="$CHECKPOINT_KEY" \
    -e TERRABASE_REGION="$region" \
    -e TERRABASE_DATABASE_PATH=/data \
    -e TERRABASE_DATABASE_ROUTE="$DATABASE" \
    -e TERRABASE_DATABASE_TENANT="$TENANT" \
    -e TERRABASE_DATABASE_ULID="$DATABASE" \
    -e TERRABASE_INTERNAL_DATABASE_ID="$DATABASE" \
    -e TERRABASE_DATABASE_BACKEND=storage-engine \
    -e TERRABASE_DATABASE_MODE=explicit \
    -e TERRABASE_DATABASE_HOME_REGION="$region" \
    -e TERRABASE_DATABASE_SELECTED_REGIONS="$region" \
    "$IMAGE" dotnet /app/TerraBase.dll >/dev/null
}

member member-gru GRU 28443 member-fra
member member-fra FRA 28444 member-nak
member member-nak SFO 28445 member-gru

echo "== wait for listeners =="
for node in member-gru member-fra member-nak; do
  for _ in $(seq 1 60); do
    if podman exec "$node" true 2>/dev/null && podman port "$node" 8443 >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done
done
sleep 3

echo "== netem ${DELAY_MS}ms on member-to-member traffic only =="
ip_of() { podman inspect --format '{{ (index .NetworkSettings.Networks "terrabase-demo").IPAddress }}' "$1"; }
for _ in $(seq 1 30); do
  IP_GRU="$(ip_of member-gru)"
  IP_FRA="$(ip_of member-fra)"
  IP_NAK="$(ip_of member-nak)"
  [ -n "$IP_GRU" ] && [ -n "$IP_FRA" ] && [ -n "$IP_NAK" ] && break
  sleep 1
done
[ -n "$IP_GRU" ] && [ -n "$IP_FRA" ] && [ -n "$IP_NAK" ] || { echo "member IPs never resolved"; exit 1; }
echo "members at gru=$IP_GRU fra=$IP_FRA nak=$IP_NAK"
shape() { # node peer1ip peer2ip
  podman exec "$1" tc qdisc replace dev eth0 root handle 1: prio bands 3 >/dev/null
  podman exec "$1" tc qdisc add dev eth0 parent 1:3 handle 30: netem delay "${DELAY_MS}ms" 5ms >/dev/null
  podman exec "$1" tc filter add dev eth0 protocol ip parent 1:0 prio 3 u32 match ip dst "$2" flowid 1:3 >/dev/null
  podman exec "$1" tc filter add dev eth0 protocol ip parent 1:0 prio 3 u32 match ip dst "$3" flowid 1:3 >/dev/null
}
shape member-gru "$IP_FRA" "$IP_NAK"
shape member-fra "$IP_GRU" "$IP_NAK"
shape member-nak "$IP_GRU" "$IP_FRA"

echo "== measure =="
"$DRIVER" measure "$CERTS" "$RESULTS/results.jsonl" --samples "$SAMPLES" --batch 128 2>&1 | tee "$RESULTS/driver.log"

echo "== raw inter-region delay check (gru -> fra, one ping's worth of tc stats) =="
for node in member-gru member-fra member-nak; do
  echo "--- $node"
  podman exec "$node" tc -s qdisc show dev eth0 parent 1:3 | head -4
done

echo
echo "results: $RESULTS/results.jsonl"
echo "teardown: podman rm -f member-gru member-fra member-nak; podman network rm $NETWORK"
