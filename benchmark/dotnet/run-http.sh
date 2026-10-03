#!/usr/bin/env bash
# End-to-end comparison over Kestrel: minimal API (RDG), minimal API (RDF) and TerraScale
# endpoints answer the same scenarios from one process; oha drives each route in turn.
#
#   bench/run-http.sh [duration] [connections] [rounds]
#
# Variants alternate within each round and the summary reports medians, so background load
# shifts all three alike instead of biasing whichever variant ran during a busy spell.
#
# Needs oha on PATH (or nix, which supplies it). Run it with `nice -n 19` locally.
set -euo pipefail

duration="${1:-10s}"
connections="${2:-64}"
rounds="${3:-3}"
port="${PORT:-5199}"
root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/bench/TerraScale.Endpoints.Benchmarks"
results="$root/bench/results"
mkdir -p "$results"

if command -v oha >/dev/null; then oha=(oha); else oha=(nix shell nixpkgs#oha -c oha); fi

dotnet build "$project" -c Release --nologo -v q
dotnet "$project/bin/Release/net11.0/TerraScale.Endpoints.Benchmarks.dll" serve --urls "http://127.0.0.1:$port" &
server=$!
trap 'kill "$server" 2>/dev/null || true' EXIT
until curl -fsS "http://127.0.0.1:$port/ts/health" >/dev/null 2>&1; do sleep 0.2; done

body="$(mktemp)"
printf '{"name":"widget"}' >"$body"
out="$results/http-$(date +%Y%m%d-%H%M%S).csv"
echo "round,scenario,variant,rps,p50_ms,p99_ms" >"$out"

for round in $(seq 1 "$rounds"); do
for scenario in health echo send scoped text; do
  for variant in rdg rdf ts; do
    request=()
    if [[ "$scenario" == echo || "$scenario" == send ]]; then
      request=(-m POST -T application/json -D "$body")
    fi
    url="http://127.0.0.1:$port/$variant/$scenario"
    # Warm up the route (JIT, tiering) before measuring.
    "${oha[@]}" -z 3s -c "$connections" --no-tui --output-format quiet "${request[@]}" "$url" >/dev/null
    "${oha[@]}" -z "$duration" -c "$connections" --no-tui --output-format json "${request[@]}" "$url" | python3 -c '
import json, sys
d = json.load(sys.stdin)
codes = d.get("statusCodeDistribution", {})
assert set(codes) == {"200"}, f"non-200 responses: {codes}"
rps = d["summary"]["requestsPerSec"]
p50 = d["latencyPercentiles"]["p50"] * 1000
p99 = d["latencyPercentiles"]["p99"] * 1000
print(f"{sys.argv[1]},{sys.argv[2]},{sys.argv[3]},{rps:.0f},{p50:.3f},{p99:.3f}")
' "$round" "$scenario" "$variant" | tee -a "$out"
  done
done
done

python3 - "$out" <<'PY'
import csv, statistics, sys
rows = list(csv.DictReader(open(sys.argv[1])))
print(f"\n{'scenario':<8} {'variant':<4} {'median rps':>11} {'vs rdg':>7} {'p50 ms':>7} {'p99 ms':>7}")
for scenario in dict.fromkeys(r["scenario"] for r in rows):
    med = {}
    for variant in ("rdg", "rdf", "ts"):
        picked = [r for r in rows if r["scenario"] == scenario and r["variant"] == variant]
        med[variant] = [statistics.median(float(r[k]) for r in picked) for k in ("rps", "p50_ms", "p99_ms")]
    for variant, (rps, p50, p99) in med.items():
        print(f"{scenario:<8} {variant:<4} {rps:>11.0f} {rps / med['rdg'][0]:>7.2f} {p50:>7.3f} {p99:>7.3f}")
PY
echo "Raw results: $out"
