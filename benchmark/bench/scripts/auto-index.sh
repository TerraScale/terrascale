#!/usr/bin/env bash
# D-31 / E-13 auto-index benchmark. Real served HTTP only; missing evidence
# fails closed and no absent measurement is converted into a passing result.
# If a Rust load-driver is supplied by an operator, invoke it as
# `nice -n 19 ...`; this harness itself is the HTTP instrument.
set -Eeuo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="${TERRABASE_BENCH_RESULTS_DIR:-$ROOT/bench/results}"
RUN="$(date +%s)-$$"; TMP="$(mktemp -d "${TMPDIR:-/tmp}/tb-auto-index.XXXXXX")"
RECEIPT="$OUT/auto-index-$RUN.json"; FACTS="$TMP/facts.tsv"
mkdir -p "$OUT"; : > "$FACTS"; trap 'rm -rf "$TMP"' EXIT
failure=""
abort() { failure="$1"; return 1 2>/dev/null || exit 1; }
on_error() { local s=$?; [[ -n "$failure" ]] || failure="command failed (status $s): ${BASH_COMMAND:-unknown}"; python3 -c 'import json,sys; json.dump({"status":"failed","family":"auto-indexing","run_id":sys.argv[2],"error":sys.argv[3],"synthetic_pass":False,"facts":open(sys.argv[4]).read().splitlines()},open(sys.argv[1],"w"),indent=2)' "$RECEIPT" "$RUN" "$failure" "$FACTS"; echo "auto-index failed: $RECEIPT" >&2; exit "$s"; }
trap on_error ERR
if [[ "${1:-}" == -h || "${1:-}" == --help ]]; then sed -n '2,8p' "$0"; exit 0; fi

required=(TERRABASE_BENCH_TERRABASE_ENDPOINT TERRABASE_BENCH_TERRABASE_DATABASE TERRABASE_BENCH_RAVEN_ENDPOINT TERRABASE_BENCH_RAVEN_DATABASE TERRABASE_BENCH_COMPARATOR_IDENTITY TERRABASE_BENCH_SHADOW_EVIDENCE_URL TERRABASE_BENCH_LOGICAL_COST_URL TERRABASE_BENCH_WRITE_BUDGET_URL)
for name in "${required[@]}"; do [[ -n "${!name:-}" ]] || abort "missing required $name"; done
command -v curl >/dev/null || abort curl-required; command -v python3 >/dev/null || abort python3-required
source "$(dirname "${BASH_SOURCE[0]}")/resource-envelope.sh"
envelope_require
TOPOLOGY="${TERRABASE_BENCH_TOPOLOGY:-}"
[[ "$TOPOLOGY" == single-node || "$TOPOLOGY" == rf2 ]] || abort "TERRABASE_BENCH_TOPOLOGY must be single-node or rf2"
RUNTIME_DIGEST="${TERRABASE_BENCH_RUNTIME_DIGEST:-}"
COMPARATOR_DIGEST="${TERRABASE_BENCH_COMPARATOR_DIGEST:-}"
[[ "$RUNTIME_DIGEST" =~ ^[[:xdigit:]]{64}$ ]] || abort "TERRABASE_BENCH_RUNTIME_DIGEST must be an immutable 64-hex digest"
[[ "$COMPARATOR_DIGEST" =~ ^[[:xdigit:]]{64}$ ]] || abort "TERRABASE_BENCH_COMPARATOR_DIGEST must be an immutable 64-hex digest"
TB="${TERRABASE_BENCH_TERRABASE_ENDPOINT%/}"; RV="${TERRABASE_BENCH_RAVEN_ENDPOINT%/}"
TBDB="$TERRABASE_BENCH_TERRABASE_DATABASE"; RVDB="$TERRABASE_BENCH_RAVEN_DATABASE"
DOCS="${AUTO_INDEX_DOCS:-1000000}"; BATCH="${AUTO_INDEX_BATCH:-500}"; SAMPLES="${AUTO_INDEX_SAMPLES:-100}"; WRITES="${AUTO_INDEX_WRITE_OPS:-1000}"; ROUNDS="${AUTO_INDEX_TEMPLATE_ROUNDS:-3}"
[[ "$DOCS$BATCH$SAMPLES$WRITES$ROUNDS" =~ ^[1-9][0-9]*$ ]] || abort invalid-count
fact() { printf '%s\t%s\t%s\n' "$1" "$2" "$3" >> "$FACTS"; }
request() { local side="$1" method="$2" url="$3" input="${4:-}" tag="$5"; local body="$TMP/$tag.body"; local args=(curl -sS --connect-timeout 10 --max-time 300 -X "$method" -o "$body" -w '%{http_code}\t%{time_total}' -H 'Accept: application/json'); [[ -n "${TERRABASE_BENCH_API_KEY:-}" ]] && args+=(-H "Authorization: Bearer $TERRABASE_BENCH_API_KEY"); [[ -n "$input" ]] && args+=(-H 'Content-Type: application/json' --data-binary "@$input"); local meta; meta="$("${args[@]}" "$url")"; fact "$tag" "$side" "status=${meta%%$'\t'*},time_seconds=${meta#*$'\t'},body=$body"; printf '%s\t%s\t%s\n' "${meta%%$'\t'*}" "${meta#*$'\t'}" "$body"; }
ok() { [[ "$1" == "$2" && -s "$3" ]] || abort "expected HTTP $1 with nonempty body, got $2"; }
sha() { sha256sum "$1" | awk '{print $1}'; }

# Identity is immutable operator pin plus server-reported bytes.
read -r c t b < <(request terrabase GET "$TB/api/v1/health" '' identity-tb-health); ok 200 "$c" "$b"; h="$(sha "$b")"
read -r c t b < <(request terrabase GET "$TB/api/v1/openapi.json" '' identity-tb-openapi); ok 200 "$c" "$b"; fact identity terrabase "pin=$TERRABASE_BENCH_COMPARATOR_IDENTITY,health_sha256=$h,openapi_sha256=$(sha "$b")"
read -r c t b < <(request ravendb GET "$RV/build/version" '' identity-raven-build); ok 200 "$c" "$b"; fact identity ravendb "pin=$TERRABASE_BENCH_COMPARATOR_IDENTITY,build_sha256=$(sha "$b")"
affinity="$(taskset -pc $$ 2>/dev/null || true)"; cgroup="$(cat /proc/self/cgroup 2>/dev/null || true)"; [[ -n "$TERRABASE_BENCH_ENVELOPE_SPEC" && -n "$affinity" && -n "$cgroup" ]] || abort missing-launch-resource-evidence; fact launch resource "envelope=$TERRABASE_BENCH_ENVELOPE_SPEC,topology=$TOPOLOGY,kernel=$(uname -srmo),affinity=$affinity,cgroup=$cgroup,runtime_digest=$RUNTIME_DIGEST,comparator_digest=$COMPARATOR_DIGEST"

mkbatch() { python3 -c 'import json,sys; a,n,o=map(int,sys.argv[1:]); json.dump({"operations":[{"op":"put","id":f"auto-{i}","value":{"name":f"auto-name-{i}","group":i%100,"tenant":f"tenant-{i%16}","region":f"r-{i%4}","status":"ready","priority":i%10,"marker":f"auto-marker-{i}"}} for i in range(a,a+n)]},open(o,"w"),separators=(",",":"))' "$1" "$2" "$3"; }
mkquery() { python3 -c 'import json,sys; json.dump({"sql":sys.argv[1],"parameters":{"name":"auto-name-1"}},open(sys.argv[2],"w"),separators=(",",":"))' "$1" "$2"; }

# Seed exactly one million documents by default through real batch/bulk APIs.
start="$(date +%s%N)"; for ((i=0;i<DOCS;i+=BATCH)); do n=$(( DOCS-i < BATCH ? DOCS-i : BATCH )); mkbatch "$i" "$n" "$TMP/tb.json"; read -r c t b < <(request terrabase POST "$TB/api/v1/databases/$TBDB/collections/users/documents/batch" "$TMP/tb.json" "seed-tb-$i"); ok 200 "$c" "$b"; python3 -c 'import json,sys; a,n,o=map(int,sys.argv[1:]); json.dump({"Commands":[{"Type":"PUT","Id":f"users/auto-{i}","Document":{"name":f"auto-name-{i}","group":i%100,"tenant":f"tenant-{i%16}","region":f"r-{i%4}","status":"ready","priority":i%10,"marker":f"auto-marker-{i}","@metadata":{"@collection":"users"}}} for i in range(a,a+n)]},open(o,"w"),separators=(",",":"))' "$i" "$n" "$TMP/rv.json"; read -r c t b < <(request ravendb POST "$RV/databases/$RVDB/bulk_docs" "$TMP/rv.json" "seed-raven-$i"); ok 200 "$c" "$b"; done
seconds="$(python3 -c 'import time; print((time.time_ns()-int("'"$start"'"))/1e9)')"; fact seed both "documents=$DOCS,seconds=$seconds"

# Write throughput points are recorded at zero, five, and twelve visible
# index fields.  The zero point is deliberately before the first query or
# registration; 12 is the D-31 collection cap and budget-ceiling point.
throughput() { local count="$1" start; start="$(date +%s%N)"; for ((j=0;j<WRITES;j+=BATCH)); do n=$(( WRITES-j < BATCH ? WRITES-j : BATCH )); mkbatch "$((DOCS+count*WRITES+j))" "$n" "$TMP/write.json"; read -r c t b < <(request terrabase POST "$TB/api/v1/databases/$TBDB/collections/users/documents/batch" "$TMP/write.json" "throughput-$count-$j"); ok 200 "$c" "$b"; done; local sec; sec="$(python3 -c 'import time; print((time.time_ns()-int("'"$start"'"))/1e9)')"; fact throughput terrabase "indexes=$count,operations=$WRITES,seconds=$sec,ops_per_second=$(python3 -c 'print('"$WRITES"'/'"$sec"')'),budget_ceiling=$([[ "$count" == 12 ]] && echo true || echo false)"; }
throughput_raven() { local count="$1" start; start="$(date +%s%N)"; for ((j=0;j<WRITES;j+=BATCH)); do n=$(( WRITES-j < BATCH ? WRITES-j : BATCH )); python3 -c 'import json,sys; a,n,o=map(int,sys.argv[1:]); json.dump({"Commands":[{"Type":"PUT","Id":f"users/throughput-{i}","Document":{"name":f"throughput-{i}","group":i%100,"marker":f"throughput-marker-{i}","@metadata":{"@collection":"users"}}} for i in range(a,a+n)]},open(o,"w"),separators=(",",":"))' "$((DOCS+count*WRITES+j))" "$n" "$TMP/rv-write.json"; read -r c t b < <(request ravendb POST "$RV/databases/$RVDB/bulk_docs" "$TMP/rv-write.json" "throughput-raven-$count-$j"); ok 200 "$c" "$b"; done; local sec; sec="$(python3 -c 'import time; print((time.time_ns()-int("'"$start"'"))/1e9)')"; fact throughput ravendb "indexes=$count,operations=$WRITES,seconds=$sec,ops_per_second=$(python3 -c 'print('"$WRITES"'/'"$sec"')'),budget_ceiling=$([[ "$count" == 12 ]] && echo true || echo false)"; }
throughput 0
throughput_raven 0

tb_query() { mkquery "$1" "$TMP/query.json"; request terrabase POST "$TB/api/v1/databases/$TBDB/sql" "$TMP/query.json" "$2"; }
rv_query() { local tag="$1" field="${2:-name}"; request ravendb GET "$RV/databases/$RVDB/queries?query=from%20users%20where%20$field%20%3D%20'auto-name-1'&pageSize=1" '' "$tag"; }
begin="$(date +%s%N)"
read -r c t b < <(tb_query 'SELECT _id,name FROM users WHERE name = :name' first-uncovered-tb); ok 200 "$c" "$b"; grep -q auto-name-1 "$b" || abort tb-first-query-no-row; fact first_uncovered terrabase "milliseconds=$(python3 -c 'print(float("'"$t"'")*1000)')"
read -r c t b < <(rv_query first-uncovered-raven); ok 200 "$c" "$b"; grep -q auto-name-1 "$b" || abort raven-first-query-no-row; fact first_uncovered ravendb "milliseconds=$(python3 -c 'print(float("'"$t"'")*1000)')"

printf '{"field":"name"}' > "$TMP/index.json"; read -r c t b < <(request terrabase POST "$TB/api/v1/databases/$TBDB/collections/users/indexes" "$TMP/index.json" create-index); [[ "$c" == 200 || "$c" == 201 ]] || abort "index registration HTTP $c"; active=false
for _ in $(seq 1 120); do read -r c t b < <(request terrabase GET "$TB/api/v1/databases/$TBDB/collections/users/indexes" '' poll-index); ok 200 "$c" "$b"; grep -q '"field":"name","state":"active"' "$b" && { active=true; break; }; sleep 1; done; [[ "$active" == true ]] || abort index-not-active
seconds="$(python3 -c 'import time; print((time.time_ns()-int("'"$begin"'"))/1e9)')"; fact time_to_active terrabase "documents=$DOCS,seconds=$seconds,state_sha256=$(sha "$b")"; read -r c t b < <(request ravendb GET "$RV/databases/$RVDB/indexes?start=0&pageSize=1024" '' raven-indexes); ok 200 "$c" "$b"; fact time_to_active ravendb "documents=$DOCS,state_sha256=$(sha "$b")"

for field in group tenant region status; do printf '{"field":"%s"}' "$field" > "$TMP/index-$field.json"; read -r c t b < <(request terrabase POST "$TB/api/v1/databases/$TBDB/collections/users/indexes" "$TMP/index-$field.json" "index-$field"); [[ "$c" == 200 || "$c" == 201 ]] || abort "five-index registration HTTP $c"; done
throughput 5; throughput_raven 5
for field in priority marker created owner email segment score; do printf '{"field":"%s"}' "$field" > "$TMP/index-$field.json"; read -r c t b < <(request terrabase POST "$TB/api/v1/databases/$TBDB/collections/users/indexes" "$TMP/index-$field.json" "index-$field"); [[ "$c" == 200 || "$c" == 201 ]] || abort "twelve-index registration HTTP $c"; done
throughput 12; throughput_raven 12

for side in terrabase ravendb; do : > "$TMP/times"; for ((i=0;i<SAMPLES;i++)); do if [[ "$side" == terrabase ]]; then read -r c t b < <(tb_query 'SELECT _id,name FROM users WHERE name = :name' "indexed-$side-$i"); else read -r c t b < <(rv_query "indexed-$side-$i"); fi; ok 200 "$c" "$b"; echo "$t" >> "$TMP/times"; done; python3 -c 'import sys; s,p,f=sys.argv[1:]; v=sorted(float(x) for x in open(p)); q=lambda x:v[max(0,min(len(v)-1,int(x*len(v)+.999999)-1))]*1000; open(f,"a").write(f"indexed_latency\\t{s}\\tp50_ms={q(.5):.3f},p95_ms={q(.95):.3f},p99_ms={q(.99):.3f},samples={len(v)}\\n")' "$side" "$TMP/times" "$FACTS"; done

# Immediate write/read markers are the zero-stale-reads correctness gate.
for side in terrabase ravendb; do for i in $(seq 1 10); do id="stale-$RUN-$side-$i"; printf '{"name":"stale","marker":"%s"}' "$id" > "$TMP/stale.json"; if [[ "$side" == terrabase ]]; then read -r c t b < <(request terrabase PUT "$TB/api/v1/databases/$TBDB/collections/users/documents/$id" "$TMP/stale.json" "stale-write-$side-$i"); else read -r c t b < <(request ravendb PUT "$RV/databases/$RVDB/docs?id=users/$id" "$TMP/stale.json" "stale-write-$side-$i"); fi; [[ "$c" == 200 || "$c" == 201 ]] || abort "stale write HTTP $c"; if [[ "$side" == terrabase ]]; then read -r c t b < <(request terrabase GET "$TB/api/v1/databases/$TBDB/collections/users/documents/$id" '' "stale-read-$side-$i"); else read -r c t b < <(request ravendb GET "$RV/databases/$RVDB/docs?id=users/$id" '' "stale-read-$side-$i"); fi; ok 200 "$c" "$b"; grep -q "$id" "$b" || abort "stale read for $id"; done; fact zero_stale_reads "$side" samples=10; done

for side in terrabase ravendb; do prev=x; for round in $(seq 1 "$ROUNDS"); do for field in name group tenant; do if [[ "$side" == terrabase ]]; then read -r c t b < <(tb_query "SELECT _id,$field FROM users WHERE $field = :name" "template-$side-$round-$field"); else read -r c t b < <(rv_query "template-$side-$round-$field" "$field"); fi; ok 200 "$c" "$b"; done; if [[ "$side" == terrabase ]]; then read -r c t b < <(request terrabase GET "$TB/api/v1/databases/$TBDB/collections/users/indexes" '' "fixed-$side-$round"); else read -r c t b < <(request ravendb GET "$RV/databases/$RVDB/indexes?start=0&pageSize=1024" '' "fixed-$side-$round"); fi; ok 200 "$c" "$b"; digest="$(sha "$b")"; fact fixed_point "$side" "round=$round,digest=$digest"; [[ "$round" == 1 || "$digest" == "$prev" ]] || abort "$side replay did not reach fixed point"; prev="$digest"; done; done

# These are durable server facts, not client-side estimates. Their absence is
# an explicit failure because D-31 requires shadow significance and logical work.
read -r c t b < <(request terrabase GET "$TERRABASE_BENCH_SHADOW_EVIDENCE_URL" '' shadow-evidence); ok 200 "$c" "$b"; python3 -c 'import json,sys; v=json.load(open(sys.argv[1])); assert all(k in v for k in ("baseline","candidate","statistically_significant","decision","threshold")); assert abs(float(v["threshold"])-.05)<1e-12; assert v["decision"] in ("activate","activate_with_pins","rollback")' "$b"; fact shadow terrabase "threshold=0.05,evidence_sha256=$(sha "$b")"
read -r c t b < <(request terrabase GET "$TERRABASE_BENCH_LOGICAL_COST_URL" '' logical-cost); ok 200 "$c" "$b"; python3 -c 'import json,sys; v=json.load(open(sys.argv[1])); assert "pages_read" in v and "rows_examined" in v' "$b"; fact logical_cost terrabase "evidence_sha256=$(sha "$b")"
read -r c t b < <(request terrabase GET "$TERRABASE_BENCH_WRITE_BUDGET_URL" '' write-budget); ok 200 "$c" "$b"; python3 -c 'import json,sys; v=json.load(open(sys.argv[1])); assert v.get("visible_indexes") == 12 and float(v["maintenance_cost"]) <= 2.0*float(v["base_cost"])' "$b"; fact write_budget terrabase "indexes=12,ceiling=2x,evidence_sha256=$(sha "$b")"

python3 -c 'import json,sys; r=[]; [r.append({"phase":p,"subject":s,"value":v}) for l in open(sys.argv[3]) if l.strip() for p,s,v in [l.rstrip("\n").split("\t",2)]]; json.dump({"status":"complete","family":"auto-indexing","run_id":sys.argv[2],"topology":sys.argv[5],"documents":int(sys.argv[4]),"regression_threshold":.05,"index_cap":12,"write_budget_multiple":2.0,"comparator_pin":sys.argv[6],"runtime_digest":sys.argv[7],"comparator_digest":sys.argv[8],"synthetic_pass":False,"facts":r},open(sys.argv[1],"w"),indent=2,sort_keys=True)' "$RECEIPT" "$RUN" "$FACTS" "$DOCS" "$TOPOLOGY" "$TERRABASE_BENCH_COMPARATOR_IDENTITY" "$RUNTIME_DIGEST" "$COMPARATOR_DIGEST"
trap - ERR; echo "auto-index complete: $RECEIPT"
