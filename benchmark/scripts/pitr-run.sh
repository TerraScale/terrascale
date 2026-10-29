#!/usr/bin/env bash
# Orchestrates one backup/PITR proof run (benchmark/docs/pitr-100m.md) with pitr-bench and PitrLab: the source run with
# the cut matrix, bucket audits, Path A and Path B restores, the fault matrix and the defect subset. G0b runs against
# PitrLab's local S3 emulator (TARGET=local-s3-emulator: LOCAL S3 EMULATOR, NOT TIGRIS); G1-G3 against Tigris
# (TARGET=tigris, SOURCE_ENV = the 0600 env file a human prepared, read by the node only).
#
#   BASE=<new run base dir> BINARY=<terrabase> BINARY_SHA256=<sha256> COMMIT=<sha> benchmark/scripts/pitr-run.sh <step> [args]
#
# The binary is a parameter, never a default: `source` refuses to start unless BINARY hashes to BINARY_SHA256, and
# out/pins.json records binary, sha256, commit, target and what the binary is known to exclude (PINS_NOTE). pitr-bench
# then refuses to start any node of the run on a binary with another hash.
#
#   matrix                the G0b cut matrix: build, source, load-a, load-b, mutations, audit src, path-a, copies pristine
#                         (fault source, never served) and pathb, path-b on pathb (every cut, target restart, --compare)
#   build                 pitr-bench into $BASE, with sha256 of its sources
#   source                serve (emulator) -> env -> init -> node start -> setup -> probe -> sampler + shipped recorder -> C0
#   load-a                load-a with bounded C1 at half, then exact C2
#   load-b                graceful restart (generation G1, snapshot S1), reconcile, load-b at once: bounded C3 at 5% (S1
#                         may still upload), CK SIGKILL at 45% while backup is busy (writer stopped, restart -> G2,
#                         reconcile, the same write resumes and proves its in-flight batches), bounded C4 at 70%, exact C5
#   load-b-plain          load-b with no restart or kill: bounded C4 at half, exact C5 (defect subset sources)
#   mutations             u1 -> C6, d1 -> C7, r1 -> C8, then the source verified exactly at C8
#   audit ROOT LABEL      PitrLab audit of the database prefix and of _system in a dir root
#   path-a                restores on the source node at C2 C5 C8 and bounded C1 C3 C4, logical verifies
#   copy LABEL            byte-exact download of the database and keys prefixes into $BASE/copies/LABEL
#   tcuts                 after the source audit: bounded cuts T<phase> and T<phase>m1 at the first segment holding u1, d1
#                         and r1 writes and 1 ms before it (the bounded rule on each mutation)
#   mutate KIND KEY LABEL  isolated mutated copy of copies/pristine (or MUTATE_FROM) + its audit
#   fault KIND KEY CUT LABEL  the mutated copy restored at CUT on a fresh target, recorded
#   fault-matrix          every fault case from the run's own audit and cuts (cmd_fault_matrix)
#   wrong-key CUT         path-b on a pristine copy with another master key
#   defects               the defect subset (cmd_defects): P1a, P1b, P2, P3, each in its own sub-run under $BASE
#   serve NAME ROOT [FAULTS_JSON] / unserve NAME   the emulator alone; envfile NAME SERVE [KEY_FROM] its 0600 node env
#                         file (prints the path); cut NAME APPLIED [PARTIAL] one cut; stop: the source node, sampler,
#                         recorder and emulator
#   p3 [DELAY_MS MAX_BATCHES]  after load-a: restart onto a slow-snapshot emulator, load-b at once (see cmd_p3)
#   restore CUT NAME LABEL [--node-dir D --env-file F] [--segments S]   one restore and its logical verify
#   a7                    decision A-7 probes on a small run (DOCS <= 50k, SPLIT=DOCS): build, source, cmd_a7, stop
#   summary               restore_summary: required successes, refusals, failures and the run's verdict
#
# Every restore's outcome is restored (201, then verified with the two-sided A-7 check), refused:<code> (a typed 409
# refusal: recorded, never verified, never a pass and never a loss) or failed. SKEW_MS (0) is the A-7 clock allowance
# between this host, which times the ledger, and the node: 0 only while both share this host's clock; a remote node
# needs a measured allowance (the samples' clock field bounds the offset from the node's Date header).
#
# MODE (required): exploratory or qualification. REQUIRED is the set of cuts (glob patterns) whose every restore must
# answer 201 and verify; fault cases never are. It is MUST_RESTORE if set, else the set the run started with (pins.json),
# else none (exploratory: never qualification evidence) or the frozen matrix below (qualification; the a7 step: C0 C2
# S*). In qualification an empty set fails before anything starts; a run never changes its mode. A typed refusal passes
# only a safety expectation (a cut outside REQUIRED: a fault case or a time the product cannot prove); it never counts
# as a restore success, and a qualification run whose restores are half or more refusals is red (restore_summary).
#
# Env: PITR_BENCH (a pitr-bench other than $BASE/pitr-bench), SRC_FAULTS (s3-serve faults JSON for the source emulator), DOCS (1000000) SPLIT (400000) PARTS (100) SEED (20261012)
# WRITERS (16) TARGET (local-s3-emulator), LAB_DLL (the built PitrLab test assembly, default the repo's Release build),
# MODE, MUST_RESTORE, SKEW_MS (0), A7_SECONDS (3).
# Every process runs at nice 19. Loads, restores and verifies hold /tmp/ts-heavy.lock with flock -o, so no child (a node,
# s3-serve, an MSBuild or compiler server) can inherit it; PitrLab runs as a built assembly, never through MSBuild.
# Nothing here deletes data.
set -euo pipefail
set +x # never trace: env files hold the master key

if [[ ${1:-} == help || ${1:-} == --help ]]; then
	cat <<'USAGE'
path-b ROOT LABEL [CUTS]: serve an isolated bucket copy, prepare the pinned tenant catalog with
PitrLab's test fixture, start the target, validate with adopt, restore and verify each cut,
then restart and compare versions. Defaults to every recorded cut. This tests tenant backup
replay; it does not recover a production catalog or prove whole-node disaster recovery.
USAGE
	exit 0
fi

REPO=$(cd "$(dirname "$0")/../.." && pwd)
: "${BASE:?BASE (run base directory) is required}"
DOCS=${DOCS:-1000000} SPLIT=${SPLIT:-400000} PARTS=${PARTS:-100} SEED=${SEED:-20261012} WRITERS=${WRITERS:-16}
TARGET=${TARGET:-local-s3-emulator}
# The qualification matrix, frozen 2026-10-12 until the restore owner's proven contract adds its transaction
# boundaries: every exact quiesced cut (each has its post-cut nudge as a later witness) and every snapshot time of the
# source's bucket (S*, snapcuts). The fault cases keep their own expected outcomes (out/fault-matrix.jsonl).
QUALIFICATION_MATRIX="C0 C2 C5 C6 C7 C8 S*" QUALIFICATION_A7="C0 C2 S*"
# A run keeps the mode and the required set it started with (out/pins.json); MUST_RESTORE, when set, overrides the set.
PINS=$BASE/out/pins.json
if [[ -s $PINS ]] && pinned=$(jq -r '.mode // empty' "$PINS") && [[ -n $pinned && $pinned != "${MODE:-}" ]]; then
	echo "this run started with MODE=$pinned" >&2
	exit 2
fi
case ${MODE:-} in
exploratory | qualification)
	if [[ -n ${MUST_RESTORE+set} ]]; then REQUIRED=$MUST_RESTORE
	elif [[ -s $PINS ]] && jq -e 'has("required")' "$PINS" >/dev/null; then REQUIRED=$(jq -r .required "$PINS")
	elif [[ $MODE == exploratory ]]; then REQUIRED=""
	elif [[ ${1:-} == a7 ]]; then REQUIRED=$QUALIFICATION_A7
	else REQUIRED=$QUALIFICATION_MATRIX; fi
	;;
*) echo "MODE must be exploratory or qualification" >&2; exit 2 ;;
esac
[[ $MODE == exploratory || -n ${REQUIRED// /} ]] || { echo "MODE=qualification needs a nonempty required-success set" >&2; exit 2; }
LAB_DLL=${LAB_DLL:-$REPO/tests/TerraBase.E2E/bin/Release/net11.0/TerraBase.E2E.dll}
PB=${PITR_BENCH:-$BASE/pitr-bench} RUN=$BASE/source # PITR_BENCH: another build (a run made by an older one)
LOCK=/tmp/ts-heavy.lock
mkdir -p "$BASE"/{out,jobs,secrets,s3}
chmod 700 "$BASE/secrets"
export DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false

# uutils date 0.12.0 prints %3N unpadded (a fraction under 100 ms comes out up to 900 ms late): %N is right.
log() { local t; t=$(date -u +%FT%T.%N) && printf '%s %s\n' "${t:0:23}Z" "$*" | tee -a "$BASE/out/steps.log" >&2; }
pb() { nice -n 19 "$PB" "$@"; }
heavy() { flock -o "$LOCK" nice -n 19 "$PB" "$@"; }
runjson() { jq -r "$1" "$RUN/run.json"; }
cutjson() { jq -r --arg c "$1" "select(.name==\$c) | $2" "$RUN/cuts.jsonl"; }
db_prefix() { echo "v1/$(runjson .organization)/$(runjson .project)/$(runjson .database_id)/"; }
keys_prefix() { echo "v1/keys/$(runjson .organization)/$(runjson .project)/"; }
acks() { local n; n=$(grep -c '"ev":"ack"' "$RUN/ledger/$1.jsonl" 2>/dev/null) || true; echo "${n:-0}"; } # 0 before the ledger exists

# lab JOB [ENV_FILE]: one PitrLab job with its master key and optional S3 credentials.
lab() {
	local job=$1 env=${2:-} out=$BASE/out/$(basename "$1" .json).out
	(
		if [[ -n $env ]]; then
			# The whole 0600 env file: the master key for every job, the bucket and credentials for an s3 source.
			# Each line exports verbatim: splitting on = would eat a base64 key's padding.
			while IFS= read -r line; do
				export "$line"
			done < <(grep -E '^(AWS_ENDPOINT_URL_S3|AWS_REGION|BUCKET_NAME|AWS_ACCESS_KEY_ID|AWS_SECRET_ACCESS_KEY|TERRABASE_MASTER_KEY|SSL_CERT_FILE)=' "$env")
		fi
		export TERRABASE_PITR_JOB=$job DOTNET_gcServer=0 DOTNET_GCHeapHardLimit=0x40000000
		nice -n 19 dotnet "$LAB_DLL" --treenode-filter '/*/*/PitrLabHost/*' --output detailed
	) >"$out" 2>&1 || { log "PitrLab job $job failed; see $out"; return 1; }
}

cmd_build() {
	(cd "$REPO/benchmark" && MISE_GO_VERSION=1.27.2 nice -n 19 go build -o "$PB" ./cmd/pitr-bench)
	(cd "$REPO/benchmark" && sha256sum cmd/pitr-bench/*.go internal/pitrbench/*.go go.mod go.sum) >"$BASE/build-sources.sha256"
	sha256sum "$PB" "$LAB_DLL" >>"$BASE/build-sources.sha256"
}

# pins: BINARY must hash to BINARY_SHA256; out/pins.json records the run's pins.
pins() {
	: "${BINARY:?BINARY is required}" "${BINARY_SHA256:?BINARY_SHA256 is required}" "${COMMIT:?COMMIT is required}"
	local sum
	read -r sum _ < <(sha256sum "$BINARY") # (cut is this script's cut step)
	[[ $sum == "$BINARY_SHA256" ]] || { log "BINARY $BINARY has sha256 $sum, not BINARY_SHA256 $BINARY_SHA256"; return 1; }
	jq -n --arg b "$BINARY" --arg s "$sum" --arg c "$COMMIT" --arg t "$TARGET" --arg n "${PINS_NOTE:-}" --arg m "$MODE" --arg r "$REQUIRED" \
		'{binary:$b,sha256:$s,commit:$c,target:$t,target_label:(if $t=="tigris" then "TIGRIS" else "LOCAL S3 EMULATOR, NOT TIGRIS" end),note:$n,mode:$m,required:$r}' >"$BASE/out/pins.json"
	log "pins: $(jq -c . "$BASE/out/pins.json")"
}

# serve NAME ROOT [FAULTS_JSON]: s3-serve in the background until unserve NAME deletes its ready file.
cmd_serve() {
	local name=$1 root=$2 faults=${3:-} ready=$BASE/s3/$1.ready.json job=$BASE/jobs/serve-$1.json
	[[ -e $ready ]] && { log "serve $name: $ready exists (already serving?)"; return 1; }
	jq -n --arg root "$root" --arg ready "$ready" --arg ca "$BASE/s3/$name-ca.pem" --argjson faults "${faults:-null}" \
		'{mode:"s3-serve",root:$root,bucket:"pitr",ready_file:$ready,ca_pem:$ca} + (if $faults then {faults:$faults} else {} end)' >"$job"
	setsid bash -c "$(declare -f lab log); BASE='$BASE' LAB_DLL='$LAB_DLL' lab '$job'" </dev/null >/dev/null 2>&1 &
	for _ in $(seq 600); do [[ -s $ready ]] && { log "serve $name: $(jq -c '{endpoint,bucket}' "$ready") faults ${faults:-none}"; return 0; }; sleep 0.5; done
	log "serve $name: no ready file after 5 minutes; see $BASE/out/serve-$name.out"
	return 1
}

cmd_unserve() {
	local ready=$BASE/s3/$1.ready.json pid
	pid=$(jq -r .pid "$ready")
	rm -- "$ready"
	while kill -0 "$pid" 2>/dev/null; do sleep 0.5; done
	log "unserve $1: stopped"
}

# envfile NAME SERVE [KEY_FROM]: the node's 0600 env file for emulator SERVE; the master key is new (source) or copied
# from KEY_FROM's env file. Dummy access keys: the emulator accepts any.
envfile() {
	local name=$1 ready=$BASE/s3/$2.ready.json from=${3:-} f=$BASE/secrets/$1.env
	(
		umask 077
		{
			echo "AWS_ENDPOINT_URL_S3=$(jq -r .endpoint "$ready")"
			echo "AWS_REGION=$(jq -r .region "$ready")"
			echo "BUCKET_NAME=$(jq -r .bucket "$ready")"
			echo "AWS_ACCESS_KEY_ID=pitr-local-dummy"
			echo "AWS_SECRET_ACCESS_KEY=pitr-local-dummy"
			echo "SSL_CERT_FILE=$(jq -r .ca_pem "$ready")"
			if [[ -n $from ]]; then grep '^TERRABASE_MASTER_KEY=' "$from"; else echo "TERRABASE_MASTER_KEY=$(head -c32 /dev/urandom | base64)"; fi
		} >"$f.partial"
		mv "$f.partial" "$f"
	)
	echo "$f"
}

# cut NAME APPLIED [PARTIAL]: a bounded cut with PARTIAL, else an exact cut (clock-free quiesce + window extension).
cut() {
	if [[ -n ${3:-} ]]; then pb cut --run "$RUN" --name "$1" --applied "$2" --partial "$3" | tee "$BASE/out/cut-$1.json"
	else pb cut --run "$RUN" --name "$1" --applied "$2" --wait "${CUT_WAIT:-30m}" | tee "$BASE/out/cut-$1.json"; fi
	log "cut $1: $(jq -c '{at_ms,kind,shipped_v0_ms,shipped_v1_ms,shipped_ms,window_ms,last_ack_ms}' "$BASE/out/cut-$1.json")"
}

# wait_acks PHASE N PID: until PHASE has N acknowledged batches (or the writer exits).
wait_acks() {
	while (($(acks "$1") < $2)); do kill -0 "$3" 2>/dev/null || return 1; sleep 0.2; done
}

status() { pb node status --run "$RUN" | tee -a "$BASE/out/status.jsonl" | jq -c '.file | {generation,logBytes,lastShippedAt,lagSeconds,pendingBytes,lastError}' 2>/dev/null || true; }

# reconcile LABEL: required after every node restart before any write, fence or cut. Without the lock: the caller
# either holds it (a running writer of this run) or nothing heavy of this run runs.
reconcile() {
	if pb reconcile --run "$RUN" >"$BASE/out/reconcile-$1.json" 2>"$BASE/out/reconcile-$1.log"; then
		log "reconcile $1: PASS $(jq -c '{totals:{expected:.totals.expected_live,actual:.totals.actual_live},seconds}' "$BASE/out/reconcile-$1.json")"
	else
		log "reconcile $1: FAIL $(tail -1 "$BASE/out/reconcile-$1.log")"
		return 1
	fi
}

cmd_source() {
	pins
	[[ -e $PB ]] || cmd_build
	if [[ $TARGET == local-s3-emulator ]]; then
		[[ -e $BASE/s3/src.ready.json ]] || cmd_serve src "$BASE/s3/src-root" "${SRC_FAULTS:-}"
		SOURCE_ENV=$(envfile source src)
	fi
	: "${SOURCE_ENV:?SOURCE_ENV is required for tigris}"
	pb init --run "$RUN" --binary "$BINARY" --commit "$COMMIT" --docs "$DOCS" --load-split "$SPLIT" --partitions "$PARTS" \
		--seed "$SEED" --target "$TARGET" --env-file "$SOURCE_ENV" --environment Production
	pb node start --run "$RUN" | tee "$BASE/out/node-start-1.json"
	pb setup --run "$RUN"
	pb probe --run "$RUN" | tee "$BASE/out/probe.txt"
	setsid nice -n 19 "$PB" sample --run "$RUN" --label source --interval 5s </dev/null >"$BASE/out/sample-source.out" 2>&1 &
	echo $! >"$BASE/out/sampler.pid"
	# The bounded cuts' inclusion bound: every distinct LastShippedAt, independent of the bucket.
	setsid nice -n 19 "$PB" shipped --run "$RUN" --label source --interval 250ms </dev/null >"$BASE/out/shipped-source.out" 2>&1 &
	echo $! >"$BASE/out/shipped.pid"
	ship_guard </dev/null >/dev/null 2>&1 &
	echo $! >"$BASE/out/ship-guard.pid"
	# The backup discovers a new database file within 10 s (ContinuousBackup.cs:58); pitr-bench cut fails until then.
	until [[ $(pb node status --run "$RUN" | jq .listed) == true ]]; do sleep 1; done
	cut C0 ""
}

# ship_guard: a run whose backup stopped shipping must not spend hours in its loads and cut waits. Every 30 s, once a
# writer or cut of the run has been running for 10 minutes: when every sampler line of the last 10 minutes
# (evidence/samples-source.jsonl, one per 5 s) shows the database's lastShippedAt unchanged (or null) with a lastError,
# it records out/ship-guard-stop.json (once) and stops every writer and cut of the run (SIGTERM: in-flight batches stay
# unresolved), so the step fails within minutes; it keeps stopping them while the stall lasts.
ship_guard() {
	local s=$RUN/evidence/samples-source.jsonl busy=0 stalled run="$PB (write|cut) --run $RUN( |\$)"
	while sleep 30; do
		pgrep -f "$run" >/dev/null || { busy=0; continue; }
		((busy > 0)) || busy=$(now_ms)
		(($(now_ms) - busy >= 600000)) || continue
		stalled=$(tail -n 150 "$s" 2>/dev/null | jq -sc --argjson since $(($(now_ms) - 600000)) 'map(select(.t >= $since)) as $w
			| select(($w | length) >= 100 and all($w[]; (.last_error // "") != "" and (.shipped_ms // 0) == ($w[0].shipped_ms // 0)))
			| {since_ms: $since, samples: ($w | length), last_shipped_ms: ($w[-1].shipped_ms // null), last_error: $w[-1].last_error}') || continue
		[[ -n $stalled ]] || continue
		if [[ ! -e $BASE/out/ship-guard-stop.json ]]; then
			jq -n --argjson s "$stalled" '$s + {reason: "backup stopped shipping: lastShippedAt unchanged with a lastError for 10 minutes while writing or cutting"}' >"$BASE/out/ship-guard-stop.json"
			log "SHIP GUARD: $(jq -c . "$BASE/out/ship-guard-stop.json"); stopping the run's writers and cuts"
		fi
		pkill -TERM -f "$run" || true
	done
}

# stop: the source node (graceful), its sampler and recorder, and the source emulator.
cmd_stop() {
	pb node stop --run "$RUN" || true
	local f
	for f in sampler shipped ship-guard; do [[ -s $BASE/out/$f.pid ]] && kill "$(cat "$BASE/out/$f.pid")" 2>/dev/null || true; done
	[[ -e $BASE/s3/src.ready.json ]] && cmd_unserve src
	return 0
}

# load_with_cut PHASE BOUNDED_CUT AT_BATCHES APPLIED_BEFORE: the phase under the lock, a bounded cut once AT_BATCHES are acked.
load_with_cut() {
	local phase=$1 cutname=$2 at=$3 pid
	flock -o "$LOCK" nice -n 19 "$PB" write --run "$RUN" --phase "$phase" --writers "$WRITERS" >"$BASE/out/write-$phase.json" 2>"$BASE/out/write-$phase.log" &
	pid=$!
	wait_acks "$phase" "$at" "$pid" && cut "$cutname" "$4" "$phase"
	wait "$pid"
}

cmd_load_a() {
	load_with_cut load-a C1 $((SPLIT / 1000 / 2)) ""
	cut C2 load-a
}

cmd_load_b_plain() {
	load_with_cut load-b C4 $(((DOCS - SPLIT) / 1000 / 2)) load-a
	cut C5 load-a,load-b
}

cmd_load_b() {
	local batches=$(((DOCS - SPLIT) / 1000)) pid
	status
	now_ms >"$BASE/out/before-restart-g1.ms"
	pb node stop --run "$RUN"
	log "graceful stop done (generation G0 ends)"
	pb node start --run "$RUN" | tee "$BASE/out/node-start-g1.json"
	status
	reconcile g1
	# Writes start as soon as the restart is reconciled: S1 is copied and uploaded while load-b runs.
	flock -o "$LOCK" nice -n 19 "$PB" write --run "$RUN" --phase load-b --writers "$WRITERS" >"$BASE/out/write-load-b.json" 2>"$BASE/out/write-load-b.log" &
	pid=$!
	log "load-b started after the G1 restart"
	wait_acks load-b $((batches / 20 > 0 ? batches / 20 : 1)) "$pid" && { status; cut C3 load-a load-b; } # never before the first ack
	wait_acks load-b $((batches * 45 / 100)) "$pid"
	# CK: the backup must be busy (something pending or lagging) when the node dies.
	for _ in $(seq 200); do
		[[ $(pb node status --run "$RUN" | jq '.file.pendingBytes > 0 or .file.lagSeconds > 0') == true ]] && break
		sleep 0.05
	done
	pb node status --run "$RUN" >"$BASE/out/ck-status-before-kill.json"
	cp "$RUN/ledger/load-b.jsonl" "$BASE/out/ck-ledger-load-b-at-kill.jsonl"
	pb node kill --run "$RUN"
	log "CK: SIGKILL at $(acks load-b) acked load-b batches; status before kill $(jq -c '.file | {pendingBytes,lagSeconds,lastShippedAt}' "$BASE/out/ck-status-before-kill.json")"
	# The writer stops too (its in-flight batches stay unresolved in the ledger): reconcile reads a quiet source, and
	# the resumed write re-sends exactly those batches, each proven by its 200 or by read after a 412/404.
	pkill -TERM -f "$PB write --run $RUN --phase load-b" || true
	wait "$pid" || true
	pb node start --run "$RUN" | tee "$BASE/out/node-start-g2.json"
	status
	reconcile g2 # no local acknowledgement may be lost to a process crash
	flock -o "$LOCK" nice -n 19 "$PB" write --run "$RUN" --phase load-b --writers "$WRITERS" >"$BASE/out/write-load-b-resume.json" 2>"$BASE/out/write-load-b-resume.log" &
	pid=$!
	log "CK: load-b resumed after the G2 restart"
	wait_acks load-b $((batches * 7 / 10)) "$pid" && cut C4 load-a load-b
	if ! wait "$pid"; then
		# A small run can finish load-b (and close its fence) before the kill: reconcile g2 proved it, nothing to resume.
		grep -q 'a closed phase never runs again' "$BASE/out/write-load-b-resume.log" || return 1
		log "CK: load-b had finished before the kill (its fence is closed): nothing to resume"
	fi
	cut C5 load-a,load-b
}

cmd_mutations() {
	local applied=load-a,load-b phase name
	for pair in u1:C6 d1:C7 r1:C8; do
		phase=${pair%%:*} name=${pair##*:} applied+=,${pair%%:*}
		heavy write --run "$RUN" --phase "$phase" --writers "$WRITERS" >"$BASE/out/write-$phase.json" 2>"$BASE/out/write-$phase.log"
		cut "$name" "$applied"
	done
	heavy verify --run "$RUN" --db pitr --cut C8 --versions exact --label source-exact | tee "$BASE/out/verify-source-C8.json"
	log "source exact at C8: $(jq -c '{passed,totals:{expected:.totals.expected_live,actual:.totals.actual_live}}' "$BASE/out/verify-source-C8.json")"
}

# rss-report: peak RSS and CPU of the source node in each write phase's window (ledger first to last event) and in each
# Path A restore's window, and every target node's peak (out/rss-per-phase.jsonl). The node stop threshold is 8 GiB.
cmd_rss_report() {
	local s=$RUN/evidence/samples-source.jsonl out=$BASE/out/rss-per-phase.jsonl phase lo hi f
	: >"$out"
	window() { [[ -s $s ]] && jq -cs --arg p "$1" --argjson lo "$2" --argjson hi "$3" '[.[] | select(.t >= $lo and .t <= $hi + 5000)] |
		{phase:$p, node:"source", samples:length, peak_rss_mib:((map(.rss) | max // 0) / 1048576 | floor), peak_cpu_pct:(map(.cpu_pct) | max // 0 | floor), data_mib:((last.data_bytes // 0) / 1048576 | floor)}' "$s" >>"$out"; }
	for phase in load-a load-b u1 d1 r1; do
		[[ -s $RUN/ledger/$phase.jsonl ]] || continue
		read -r lo hi < <(jq -rs '"\(min_by(.t).t) \(max_by(.t).t)"' "$RUN/ledger/$phase.jsonl")
		window "$phase" "$lo" "$hi"
	done
	for f in "$RUN"/evidence/restore-pa*.json; do
		[[ -s $f ]] || continue
		read -r lo hi < <(jq -r '"\(.started_ms) \(.started_ms + (.seconds * 1000 | floor))"' "$f")
		window "$(basename "$f" .json)" "$lo" "$hi"
	done
	for f in "$RUN"/evidence/samples-t-*.jsonl; do
		[[ -s $f ]] && jq -cs --arg n "$(basename "$f" .jsonl)" '{phase:$n[8:], node:"target", peak_rss_mib:((map(.rss) | max) / 1048576 | floor)}' "$f" >>"$out"
	done
	log "rss per phase: $(jq -c '{phase,peak_rss_mib}' "$out" | tr '\n' ' ')"
}

# tcuts: T is the time of the first segment (source audit) with at least two of the phase's writes (upserts for u1 and
# r1, deletes for d1) at or after its first send: markers, fences and nudges are single writes.
cmd_tcuts() {
	local applied=load-a,load-b phase kind first t
	for phase in u1 d1 r1; do
		kind=upserts
		[[ $phase == d1 ]] && kind=deletes
		first=$(jq -s '[.[] | select(.ev=="start") | .t] | min' "$RUN/ledger/$phase.jsonl")
		t=$(jq --argjson f "$first" --arg k "$kind" '[.generations[].segments[] | select(.at_ms >= $f and .[$k] >= 2) | .at_ms] | min' "$BASE/out/audit-src-db.json")
		if [[ $t == null ]]; then
			log "tcuts: no segment holds $phase's writes after its first send $first"
		else
			pb cut --run "$RUN" --name "T$phase" --applied "$applied" --partial "$phase" --at-ms "$t" >"$BASE/out/cut-T$phase.json"
			pb cut --run "$RUN" --name "T${phase}m1" --applied "$applied" --partial "$phase" --at-ms $((t - 1)) >"$BASE/out/cut-T${phase}m1.json"
			log "tcuts: $phase's first segment at $t (first send $first): bounded cuts T$phase and T${phase}m1"
		fi
		applied+=,$phase
	done
}

# audit ROOT LABEL
# Only the source root lives on Tigris; downloaded and mutated copies remain local directories.
pitrsrc() {
	if [[ $TARGET == tigris && $1 == "$BASE/s3/src-root" ]]; then
		echo '{"kind":"s3"}'
	else
		jq -nc --arg root "$1" '{kind:"dir",root:$root}'
	fi
}

cmd_audit() {
	local root=$1 label=$2
	jq -nc --argjson src "$(pitrsrc "$root")" --arg db "$(db_prefix)" --arg keys "$(keys_prefix)" --arg out "$BASE/out/audit-$label-db.json" \
		'{mode:"audit",source:$src,database_prefix:$db,keys_prefix:$keys,out:$out}' >"$BASE/jobs/audit-$label-db.json"
	jq -nc --argjson src "$(pitrsrc "$root")" --arg out "$BASE/out/audit-$label-system.json" \
		'{mode:"audit",source:$src,database_prefix:"v1/_system/",keys_prefix:"v1/keys/_system/",out:$out}' >"$BASE/jobs/audit-$label-system.json"
	for part in db system; do
		local lock=(flock -o "$LOCK")
		[[ -n ${NO_LOCK:-} ]] && lock=()
		"${lock[@]}" bash -c "$(declare -f lab log); BASE='$BASE' LAB_DLL='$LAB_DLL' lab '$BASE/jobs/audit-$label-$part.json' '${AUDIT_ENV:-${SOURCE_ENV:-$BASE/secrets/source.env}}'"
		log "audit $label $part: $(jq -c '{verdict,problems:[.problems[].kind],totals}' "$BASE/out/audit-$label-$part.json")"
	done
}

# segargs CUT [AUDIT]: every verify is checked against the audit of the bucket it restored from (default the source's):
# its problems fail the verify, and so does any observed shipped read missing from it (a lost final segment included);
# a bounded cut also takes its inclusion bound from the shipped history.
segargs() {
	local audit=${2:-$BASE/out/audit-src-db.json} shipped=$RUN/evidence/shipped-source.jsonl
	[[ -s $audit ]] || return 0
	echo --segments "$audit"
	[[ -s $shipped ]] && echo --shipped "$shipped"
	return 0
}

restore_verify() { # restore_verify CUT NAME LABEL [--node-dir D --env-file F] [verify args]
	local c=$1 name=$2 label=$3
	shift 3
	local nodeargs=() vargs=() must=() at=${AT_MS:+ (at-ms $AT_MS)}${AT_LATEST:+ (at the window latest)}
	while (($#)); do case $1 in --node-dir | --env-file) nodeargs+=("$1" "$2"); shift 2 ;; *) vargs+=("$1"); shift ;; esac; done
	required "$c" && [[ -z ${FAULT:-} ]] && must=(--must-restore) # a fault case has its own expected outcome
	if heavy restore --run "$RUN" --cut "$c" --name "$name" ${AT_MS:+--at-ms "$AT_MS"} ${AT_LATEST:+--at-window-latest} "${must[@]}" "${nodeargs[@]}" >"$BASE/out/restore-$name.json" 2>"$BASE/out/restore-$name.log"; then
		log "restore $name at $c$at: $(jq -c '{outcome,status,point_in_time,seconds}' "$BASE/out/restore-$name.json")"
		# A typed refusal created nothing: there is nothing to verify, and it is neither a pass nor a loss.
		[[ $(jq -r .outcome "$BASE/out/restore-$name.json") == refused:* ]] && return 0
	else
		log "restore $name at $c$at FAILED: $(jq -c '{outcome,status,point_in_time,seconds,answer}' "$BASE/out/restore-$name.json" 2>/dev/null) $(tail -1 "$BASE/out/restore-$name.log")"
		return 1
	fi
	local nd=()
	[[ ${#nodeargs[@]} -gt 0 ]] && nd=(--node-dir "${nodeargs[1]}")
	verify_db "$c" "$name" "$label" "${nd[@]}" "${vargs[@]}"
}

verify_db() { # verify_db CUT DB LABEL [--node-dir D] [verify args]
	local c=$1 name=$2 label=$3
	shift 3
	if heavy verify --run "$RUN" --db "$name" --cut "$c" --versions logical --label "$label" --skew-ms "${SKEW_MS:-0}" "$@" >"$BASE/out/verify-$name-$label.json" 2>"$BASE/out/verify-$name-$label.log"; then
		log "verify $name at $c ($label): PASS $(jq -c '{totals:{expected:.totals.expected_live,actual:.totals.actual_live},bounded,a7,shipped,compare,seconds}' "$BASE/out/verify-$name-$label.json")"
	else
		log "verify $name at $c ($label): FAIL $(jq -c '{problems,totals:{expected:.totals.expected_live,actual:.totals.actual_live,missing:.totals.missing,extra:.totals.extra},a7,markers}' "$BASE/out/verify-$name-$label.json" 2>/dev/null) $(tail -1 "$BASE/out/verify-$name-$label.log")"
		return 1
	fi
}

cmd_path_a() {
	local c
	# shellcheck disable=SC2046
	for c in C2 C5 C8; do restore_verify "$c" "pa${c,,}" path-a $(segargs "$c") || true; done
	# shellcheck disable=SC2046 # segargs is a word list
	for c in C1 C3 C4; do [[ -n $(cutjson "$c" .name) ]] && { restore_verify "$c" "pa${c,,}" path-a $(segargs "$c") || true; }; done
}

cmd_copy() {
	local dest=$BASE/copies/$1 job=$BASE/jobs/download-$1.json
	mkdir -p "$BASE/copies"
	jq -nc --argjson src "$(pitrsrc "$BASE/s3/src-root")" --arg db "$(db_prefix)" --arg keys "$(keys_prefix)" --arg root "$dest" --arg m "$BASE/out/download-$1-manifest.json" \
		'{mode:"download",source:$src,prefixes:[$db,$keys],root:$root,manifest:$m}' >"$job"
	bash -c "$(declare -f lab log); BASE='$BASE' LAB_DLL='$LAB_DLL' lab '$job' '${SOURCE_ENV:-}'" # a local copy of under a GB: short, no lock
	log "copy $1: $(grep -m1 '^download:' "$BASE/out/download-$1.out" || true)"
}

# tstart LABEL N: (re)start a target node with workstation GC and a 2 GiB heap cap (the shared host's memory budget).
tstart() {
	DOTNET_gcServer=0 DOTNET_GCHeapHardLimit=${TARGET_HEAP_LIMIT:-0x80000000} pb node start --run "$RUN" --node-dir "$BASE/targets/$1" \
		--env-file "$BASE/secrets/t-$1.env" >"$BASE/out/target-$1-start-$2.json"
}

target_up() {
	local label=$1 root=$2 from=${3:-${SOURCE_ENV:-$BASE/secrets/source.env}} tdir=$BASE/targets/$1 pid
	mkdir -p "$BASE/targets"
	if [[ ! -e $BASE/s3/t-$label.ready.json ]]; then
		cmd_serve "t-$label" "$root"
		envfile "t-$label" "t-$label" "$from" >/dev/null
	fi
	if [[ ! -s $tdir/catalog-ready.json ]]; then
		jq -n --arg data "$tdir/data" --arg out "$tdir/catalog-ready.json" \
			--slurpfile run "$RUN/run.json" \
			'{mode:"prepare-target",data_directory:$data,out:$out,target:($run[0] | {organization,project,database:.database_id,name:.database,owner})}' \
			>"$BASE/jobs/prepare-target-$label.json"
		lab "$BASE/jobs/prepare-target-$label.json" "$BASE/secrets/t-$label.env"
	fi
	pid=$(jq -r '.pid // 0' "$tdir/node.json" 2>/dev/null || echo 0)
	# (kill -0 0 would signal this process group and succeed)
	((${pid:-0} > 0)) && kill -0 "$pid" 2>/dev/null || tstart "$label" "$(date +%s)"
	pb adopt --run "$RUN" --node-dir "$tdir"
	log "target $label up: $(jq -c '{pid,internal_port}' "$tdir/node.json")"
}

# A fault case's target (NO_RESTART) is killed: its graceful stop ships its own backup of the restored database into a
# throwaway copy (minutes at 1M), which nothing reads. The clean Path B target stops gracefully.
target_down() {
	if [[ -n ${NO_RESTART:-} ]]; then pb node kill --run "$RUN" --node-dir "$BASE/targets/$1" || true
	else pb node stop --run "$RUN" --node-dir "$BASE/targets/$1" || true; fi
	cmd_unserve "t-$1"
	rss_max "t-$1"
}

# sample_target LABEL: RSS, CPU and data size of a target node every 2 s (evidence/samples-t-LABEL.jsonl); the sampler
# stops the node above pitr-bench's 8 GiB RSS threshold. It exits with its node.
sample_target() {
	setsid nice -n 19 "$PB" sample --run "$RUN" --node-dir "$BASE/targets/$1" --label "t-$1" --interval 2s </dev/null >>"$BASE/out/sample-t-$1.out" 2>&1 &
}

# rss_max LABEL: the node's peak RSS in its samples, logged (the per-phase record; the source's is per phase window).
rss_max() {
	local f=$RUN/evidence/samples-$1.jsonl
	[[ -s $f ]] && log "rss $1: peak $(jq -s 'max_by(.rss) | "\(.rss / 1048576 | floor) MiB at \(.t)"' "$f")"
	[[ -e $RUN/evidence/rss-stop-$1.json ]] && log "rss $1: STOPPED over the threshold: $(jq -c . "$RUN/evidence/rss-stop-$1.json")"
	return 0
}

# window_nudges NODE_DIR DB CUT: the restored DB's window nudges of CUT (written after CUT, never in the oracle): a
# restore at the final segment's time holds them exactly when that segment was read.
window_nudges() (
	local n peer_header
	peer_header=$(mktemp)
	trap 'rm -f -- "$peer_header"' EXIT
	chmod 600 "$peer_header"
	jq -er '.peer_secret | strings | select(length == 64 and test("^[0-9a-fA-F]{64}$")) | "X-TerraBase-Peer-Credential: " + .' "$1/node.json" > "$peer_header" || return 1
	n=$(curl -q -s --max-redirs 0 -H "@$peer_header" --http2-prior-knowledge -X POST -H 'Content-Type: application/json' \
		"http://127.0.0.1:$(jq .internal_port "$1/node.json")/v1/projects/$(runjson .project)/databases/$2/collections/nudges/query" \
		-d "{\"filter\":{\"and\":[{\"eq\":[\"/cut\",\"$3\"]},{\"eq\":[\"/why\",\"window\"]}]},\"limit\":1000}" | jq '.items | length' 2>/dev/null) || true
	log "window nudges of $3 in $2: ${n:-query failed}"
)

cmd_path_b() {
	local root=$1 label=$2 cuts=${3:-$(jq -r .name "$RUN/cuts.jsonl" | tr '\n' ' ')} tdir=$BASE/targets/$2 env=$BASE/secrets/t-$2.env c name
	local audit=${PATHB_AUDIT:-$BASE/out/audit-$label-db.json} # a mutated copy's own audit, else the source's
	[[ -s $audit ]] || audit=$BASE/out/audit-src-db.json
	local inj=() # a fault case's injected object: the audit's problems on it are expected, any other fails
	[[ -s $BASE/out/mutate-$label.json ]] && inj=(--injected-key "$(jq -r '.new_key // .key' "$BASE/out/mutate-$label.json")")
	target_up "$label" "$root"
	sample_target "$label"
	for c in $cuts; do
		[[ -n $(cutjson "$c" .name) ]] || continue
		name=${label//-/}${c,,}
		# shellcheck disable=SC2046
		restore_verify "$c" "$name" "$label-pre" --node-dir "$tdir" --env-file "$env" $(segargs "$c" "$audit") "${inj[@]}" || true
		[[ -n ${AT_MS:-}${AT_LATEST:-} ]] && window_nudges "$tdir" "$name" "$c"
	done
	# Fault cases (NO_RESTART=1) stop here: the restart re-verification belongs to the clean Path B.
	[[ -n ${NO_RESTART:-} ]] && { target_down "$label"; return 0; }
	pb node stop --run "$RUN" --node-dir "$tdir"
	tstart "$label" restart
	sample_target "$label"
	for c in $cuts; do
		name=${label//-/}${c,,}
		[[ -s $RUN/evidence/verify-$name-$c-$label-pre.json ]] || continue
		# shellcheck disable=SC2046
		verify_db "$c" "$name" "$label-post" --node-dir "$tdir" --compare "$RUN/evidence/verify-$name-$c-$label-pre.json" $(segargs "$c" "$audit") || true
	done
	target_down "$label"
}

# mutate KIND KEY LABEL: an isolated mutated copy of MUTATE_FROM (default the pristine copy), and its audit.
cmd_mutate() {
	local kind=$1 key=$2 label=$3 to=$BASE/faults/$3 job=$BASE/jobs/mutate-$3.json
	mkdir -p "$BASE/faults"
	jq -n --arg from "${MUTATE_FROM:-$BASE/copies/pristine}" --arg to "$to" --arg kind "$kind" --arg key "$key" --arg out "$BASE/out/mutate-$label.json" \
		'{mode:"mutate",from:$from,to:$to,mutation:{kind:$kind,key:$key},out:$out}' >"$job"
	lab "$job"
	log "fault $label: $(jq -c '{kind,key,new_key}' "$BASE/out/mutate-$label.json")"
	[[ -n ${NO_AUDIT:-} ]] || AUDIT_ENV="${SOURCE_ENV:-$BASE/secrets/source.env}" NO_LOCK=1 cmd_audit "$to" "$label" || true
}

# fault KIND KEY CUT LABEL: the mutated copy (made now unless mutate made it) restored at CUT on a fresh target.
cmd_fault() {
	local FAULT=1
	[[ -d $BASE/faults/$4 ]] || cmd_mutate "$1" "$2" "$4"
	NO_RESTART=1 cmd_path_b "$BASE/faults/$4" "$4" "$3"
}

cmd_wrong_key() {
	local c=$1 other=$BASE/secrets/wrong-key.env FAULT=1
	(umask 077 && echo "TERRABASE_MASTER_KEY=$(head -c32 /dev/urandom | base64)" >"$other")
	local label=wrongkey tdir=$BASE/targets/wrongkey
	mkdir -p "$BASE/faults"
	cp -al "$BASE/copies/pristine" "$BASE/faults/wrongkey"
	target_up "$label" "$BASE/faults/wrongkey" "$other" || { log "wrong-key: target did not come up (see $tdir/logs/node.log)"; return 0; }
	restore_verify "$c" "wrongkey${c,,}" wrongkey --node-dir "$tdir" --env-file "$BASE/secrets/t-$label.env" || true
	target_down "$label"
}

# p3 [DELAY_MS MAX_BATCHES]: continuous backup under concurrent writes. Graceful restart onto an emulator that delays
# this database's snapshot PUTs (and every part of a multipart snapshot), reconcile, then load-b at once (no pause)
# while the snapshot is held. Bounded cut P1 at 3/4 of the batches; after the writer, wait for backup to ship past the
# last ack, then the exact cut P2. The run must have only load-a written.
cmd_p3() {
	local delay=${1:-780000} max=${2:-0} pid last shipped deadline total
	total=$(((DOCS - SPLIT) / 1000))
	((max > 0)) && total=$max
	status
	pb node stop --run "$RUN" || true
	cmd_unserve src
	cmd_serve src "$BASE/s3/src-root" "{\"put_delay_ms\":$delay,\"key_regex\":\"^$(db_prefix)snapshots/\"}"
	envfile source src "$BASE/secrets/source.env" >/dev/null
	pb node start --run "$RUN" | tee "$BASE/out/node-start-p3.json"
	reconcile p3
	flock -o "$LOCK" nice -n 19 "$PB" write --run "$RUN" --phase load-b --writers "$WRITERS" --max-batches "$max" >"$BASE/out/write-load-b.json" 2>"$BASE/out/write-load-b.log" &
	pid=$!
	log "p3: load-b started right after the restart; snapshot PUTs (and parts) delayed $delay ms"
	wait_acks load-b $((total * 3 / 4)) "$pid" && { status; cut P1 load-a load-b; }
	wait "$pid" || log "p3: writer exited non-zero: $(tail -1 "$BASE/out/write-load-b.log")"
	status
	last=$(jq -s '[.[] | select(.ev=="ack" or .ev=="present") | .t] | max' "$RUN/ledger/load-b.jsonl")
	deadline=$(($(date +%s) + ${P3_CATCHUP_S:-900}))
	until shipped=$(pb node status --run "$RUN" | jq -r '.file.lastShippedAt // empty | sub("\\.[0-9]+";"") | fromdateiso8601? // empty') \
		&& [[ -n $shipped ]] && ((shipped * 1000 > last)); do
		(($(date +%s) > deadline)) && { log "p3: backup did not ship past the last ack ($last) in ${P3_CATCHUP_S:-900}s"; break; }
		sleep 5
	done
	status
	pb node status --run "$RUN" >"$BASE/out/p3-status-after.json"
	cp "$BASE/s3/src.ready.json.log" "$BASE/out/p3-serve-faults.log" 2>/dev/null || true
	# The exact cut needs the phase complete and fenced; a capped write (MAX_BATCHES) leaves it open: no cut then.
	if [[ $(jq -r .fenced "$BASE/out/write-load-b.json") == true ]]; then CUT_WAIT=${P3_CUT_WAIT:-20m} cut P2 load-a,load-b || log "p3: exact cut P2 FAILED"; fi
}

# defects: the defect subset, each case in its own sub-run (own emulator, node, bucket), for one binary. Run on the
# pinned binary these are DEFECT DEMONSTRATIONS (they are expected to show P1, P2, P3); run on a fixed binary they are
# the per-fix regressions, which must show the correct behaviour (a typed refusal or an exact restore; backup keeping
# up). The outcome is recorded either way; nothing here turns a defect into a pass.
#   p1: 100k docs (load-a 40k, load-b 60k, no restart), copies of its bucket:
#       P1a  every segment from the last load-a acknowledgement to C2 deleted (the last segments at or before T=C2):
#            restore at C2 on a fresh target, verified exactly against C2 (control: the pristine copy)
#       P1b  the final segment deleted: restore at that segment's time; the restored database must hold C5's window
#            nudges exactly when the segment was read (control: the pristine copy at the same time)
#   p2: 40k docs, every 5th segment PUT of the tenant stored then answered 500; restores at C1 and C2 (Path A), audit
#   p2b: the same with a burst: the tenant's segment PUTs 10 to 17 stored then answered 500
#   p3: 20k + 400k docs (about 350 MiB of change log: the 256 MiB backlog needs at least ~300k docs), the snapshot after
#       a graceful restart held 13 minutes while load-b runs (cmd_p3)
cmd_defects() {
	local parent=$BASE case=${1:-all}
	if [[ $case == all || $case == p1 ]]; then
		(BASE=$parent/p1 DOCS=100000 SPLIT=40000 PARTS=20 defect_p1)
	fi
	if [[ $case == p1-restores ]]; then # resume p1 after its copy
		(BASE=$parent/p1 DOCS=100000 SPLIT=40000 PARTS=20 && subrun && defect_p1_restores)
	fi
	if [[ $case == all || $case == p2 ]]; then
		(BASE=$parent/p2 DOCS=40000 SPLIT=40000 PARTS=20 \
			SRC_FAULTS='{"put_store_then_fail_every":5,"key_regex":"^v1/[0-9A-Z]{26}/[0-9A-Z]{26}/[0-9A-Z]{26}/wal/"}' defect_p2)
	fi
	if [[ $case == all || $case == p2b ]]; then
		# Isolated stored-then-500 PUTs are absorbed by the SDK's retry of the same key (p2); a burst of 8 outlasts the
		# SDK's attempts for one segment, so the shipper itself retries: the case P2 describes.
		(BASE=$parent/p2b DOCS=40000 SPLIT=40000 PARTS=20 \
			SRC_FAULTS='{"put_store_then_fail_from":10,"put_store_then_fail_count":8,"key_regex":"^v1/[0-9A-Z]{26}/[0-9A-Z]{26}/[0-9A-Z]{26}/wal/"}' defect_p2)
	fi
	if [[ $case == all || $case == p3 ]]; then
		(BASE=$parent/p3 DOCS=420000 SPLIT=20000 PARTS=50 defect_p3)
	fi
}

# subrun: the sub-run's own paths (BASE changed in a subshell) and the parent's pitr-bench.
subrun() {
	PB=$BASE/pitr-bench RUN=$BASE/source
	mkdir -p "$BASE"/{out,jobs,secrets,s3}
	chmod 700 "$BASE/secrets"
	cp "$(dirname "$BASE")/pitr-bench" "$PB" 2>/dev/null || cmd_build
}

# isolated LABEL: a hard-linked copy of the pristine copy for one target (a target node backs itself up into the root
# it is served from; new objects and replacements land in the copy only).
isolated() {
	mkdir -p "$BASE/faults"
	[[ -d $BASE/faults/$1 ]] || cp -al "$BASE/copies/pristine" "$BASE/faults/$1"
	echo "$BASE/faults/$1"
}

defect_p1() {
	subrun
	cmd_source
	cmd_load_a
	cmd_load_b_plain
	cmd_stop
	rss_max source
	cmd_audit "$BASE/s3/src-root" src
	cmd_copy pristine
	defect_p1_restores
}

# delete_range LABEL LO HI: a mutated copy of the pristine copy without every segment whose time is in [LO, HI] (one
# mutate per segment, chained through hard-linked copies), audited; its root is faults/LABEL-<last>. Prints the root.
delete_range() {
	local label=$1 keys k prev=$BASE/copies/pristine
	mapfile -t keys < <(jq -r --argjson lo "$2" --argjson hi "$3" '.generations[].segments[] | select(.at_ms >= $lo and .at_ms <= $hi) | .key' "$BASE/out/audit-src-db.json")
	log "$label: ${#keys[@]} segments with times in [$2, $3]: ${keys[*]}"
	((${#keys[@]} > 0)) || return 1
	for k in "${!keys[@]}"; do
		[[ -d $BASE/faults/$label-$k ]] || MUTATE_FROM=$prev NO_AUDIT=1 cmd_mutate delete_segment "${keys[$k]}" "$label-$k" >&2
		prev=$BASE/faults/$label-$k
	done
	AUDIT_ENV="${SOURCE_ENV:-$BASE/secrets/source.env}" NO_LOCK=1 cmd_audit "$prev" "$label" >&2 || true
	echo "$prev"
}

# fault-matrix: every fault case from this run's own audit and cuts, each restored on a fresh target (no restart),
# recorded in out/fault-matrix.jsonl with what correct behaviour is. On the pinned binary these are defect
# demonstrations; on a fixed binary, per-fix regressions.
cmd_fault_matrix() {
	local audit=$BASE/out/audit-src-db.json last mid mid2 snap final final_at first_final lo root FAULT=1
	last=$(jq -c '[.generations[] | select(.segments | length > 0)] | max_by(.segments | map(.at_ms) | max)' "$audit")
	mid=$(jq -r '.segments | .[length / 2 | floor].key' <<<"$last")
	mid2=$(jq -r '.segments | .[length / 2 | floor + 1].key' <<<"$last")
	snap=$(jq -r '.snapshots[0].key' <<<"$last")
	read -r final final_at < <(jq -r '.segments | max_by(.at_ms) | "\(.key) \(.at_ms)"' <<<"$last")
	first_final=$(jq -r '[.generations[] | select(.segments | length > 0)] | min_by(.segments | map(.at_ms) | min) | .segments | max_by(.at_ms).key' "$audit")
	expect() { jq -nc --arg case "$1" --arg cut "$2" --arg key "$3" --arg correct "$4" '{case:$case,cut:$cut,key:$key,correct:$correct}' >>"$BASE/out/fault-matrix.jsonl"; }
	expect fmid C8 "$mid" "typed refusal (gap): never 201 without the segment's writes"
	cmd_fault delete_segment "$mid" C8 fmid || true
	lo=$(($(cutjson C5 .last_ack_ms) - 2000))
	expect flast C5 "segments in [$lo, C5]" "typed refusal: the last segments at or before T are missing (P1)"
	root=$(delete_range flast "$lo" "$(cutjson C5 .at_ms)") && NO_RESTART=1 PATHB_AUDIT=$BASE/out/audit-flast-db.json cmd_path_b "$root" flast C5 || true
	if [[ $first_final != "$final" ]]; then
		expect ffinal0 C2 "$first_final" "typed refusal or exact: the first generation's final segment is missing"
		cmd_fault delete_segment "$first_final" C2 ffinal0 || true
	fi
	expect fdelsnap C5 "$snap" "typed refusal, or exact from another generation"
	cmd_fault delete_snapshot "$snap" C5 fdelsnap || true
	expect fcorrupt C8 "$mid" "typed refusal (authentication)"
	cmd_fault corrupt_segment "$mid" C8 fcorrupt || true
	expect ftrunc C8 "$mid2" "typed refusal (authentication)"
	cmd_fault truncate_segment "$mid2" C8 ftrunc || true
	expect fstray C8 "${mid%/*}/0000000400000000-stray" "exact (the stray key ignored) or typed refusal"
	cmd_fault stray_key "${mid%/*}/0000000400000000-stray" C8 fstray || true
	expect fdup C8 "$mid" "exact or typed refusal (duplicate start)"
	cmd_fault duplicate_segment "$mid" C8 fdup || true
	expect fcorsnap C5 "$snap" "typed refusal (authentication)"
	cmd_fault corrupt_snapshot "$snap" C5 fcorsnap || true
	# A-7: the final read's own time is unprovable (its commits during the read are in no segment and nothing follows);
	# the disaster-recovery latest point is the whole second at or before that read's mark, which the window reports.
	expect fctl "C8 at $final_at" "-" "safety: the pristine copy at the final segment's own time: typed refusal (backup_history_incomplete), or exact; never 201 without data"
	AT_MS=$final_at NO_RESTART=1 cmd_path_b "$(isolated fctl)" fctl C8 || true
	expect fdr "C8 at the window's latest" "-" "control: the pristine copy at the DR latest point (the whole second at or before the final segment's read mark: the window's latest): exact"
	AT_LATEST=1 NO_RESTART=1 cmd_path_b "$(isolated fdr)" fdr C8 || true
	log "fdr: restored at $(jq -r '.point_in_time // "?"' "$BASE/out/restore-fdrc8.json" 2>/dev/null) (the window's latest); the final segment was read at $final_at"
	expect ffinal "C8 at $final_at" "$final" "typed refusal at the final segment's time; never 201 without it"
	AT_MS=$final_at cmd_fault delete_segment "$final" C8 ffinal || true
	expect wrongkey C2 "-" "typed refusal (cannot decrypt)"
	cmd_wrong_key C2 || true
}

defect_p1_restores() {
	local lo hi keys final final_at k prev
	# From 2 s before the last load-a acknowledgement (its batch's segment) to C2: C2's marker is always among them.
	lo=$(($(cutjson C2 .last_ack_ms) - 2000)) hi=$(cutjson C2 .at_ms)
	mapfile -t keys < <(jq -r --argjson lo "$lo" --argjson hi "$hi" '.generations[].segments[] | select(.at_ms >= $lo and .at_ms <= $hi) | .key' "$BASE/out/audit-src-db.json")
	log "P1a: ${#keys[@]} segments with times in [$lo, C2 $hi]: ${keys[*]}"
	NO_RESTART=1 cmd_path_b "$(isolated control)" control "C2 C5"
	if ((${#keys[@]} > 0)); then
		prev=$BASE/copies/pristine
		for k in "${!keys[@]}"; do
			[[ -d $BASE/faults/p1a-$k ]] || MUTATE_FROM=$prev NO_AUDIT=1 cmd_mutate delete_segment "${keys[$k]}" "p1a-$k"
			prev=$BASE/faults/p1a-$k
		done
		AUDIT_ENV="${SOURCE_ENV:-$BASE/secrets/source.env}" NO_LOCK=1 cmd_audit "$prev" p1a || true
		NO_RESTART=1 cmd_path_b "$prev" p1a C2
	fi
	read -r final final_at < <(jq -r '[.generations[].segments[]] | max_by(.at_ms) | "\(.key) \(.at_ms)"' "$BASE/out/audit-src-db.json")
	log "P1b: final segment $final at $final_at (C5 window ends $(cutjson C5 .window_ms))"
	AT_MS=$final_at NO_RESTART=1 cmd_path_b "$(isolated p1bctl)" p1bctl C5
	[[ -d $BASE/faults/p1b ]] || cmd_mutate delete_segment "$final" p1b
	AT_MS=$final_at NO_RESTART=1 cmd_path_b "$BASE/faults/p1b" p1b C5
}

defect_p2() {
	subrun
	cmd_source
	cmd_load_a || log "p2: load-a or its cuts FAILED"
	status
	cmd_audit "$BASE/s3/src-root" src || true
	local c
	# shellcheck disable=SC2046
	for c in C1 C2; do [[ -n $(cutjson "$c" .name) ]] && { restore_verify "$c" "pa${c,,}" path-a $(segargs "$c") || true; }; done
	cmd_stop
	log "faults applied: $(grep -c ' fault ' "$BASE/s3/src.ready.json.log" || true) ($(grep -m3 ' fault ' "$BASE/s3/src.ready.json.log" | tr '\n' ' '))"
}

defect_p3() {
	subrun
	cmd_source
	cmd_load_a
	cmd_p3 "${P3_DELAY_MS:-780000}" 0 || log "p3: FAILED"
	cmd_stop
	cmd_rss_report
	cmd_audit "$BASE/s3/src-root" src || true
}

now_ms() { local t=${EPOCHREALTIME/./}; echo $((t / 1000)); } # the host clock, which also times the ledger (not date +%s%3N: see log)

# sleep_until MS: until the host clock is past MS.
sleep_until() {
	local d=$(($1 - $(now_ms) + 20))
	((d > 0)) && sleep "$((d / 1000)).$(printf %03d $((d % 1000)))"
	return 0
}

# next_second: the next whole second (ms), printed once it has passed.
next_second() {
	local t=$((($(now_ms) / 1000 + 1) * 1000))
	sleep_until "$t"
	echo "$t"
}

# a7cut NAME AT_MS: a bounded cut of load-a at a time already past (a bounded cut records only its time).
a7cut() {
	pb cut --run "$RUN" --name "$1" --applied "" --partial load-a --at-ms "$2" >"$BASE/out/cut-$1.json"
	log "a7: cut $1 at $2 ($(($2 % 1000)) ms into its second)"
}

# a7: decision A-7 probes over load-a. During the load: a bounded pair at a whole second W and W+500 ms (A7w0, A7h0),
# then A7u, restored on the source at once (no lock: the writer holds it) while its final segment may still be
# unshipped. After it: the exact cut C2 (T on a whole second, nothing expected of the answer) and the ledger-timed cuts
# (a7_ledger_cuts). Every cut is restored on the source (Path A); each 201 is verified with the two-sided A-7 check,
# each typed refusal recorded; a7_report gives the verdict.
cmd_a7() {
	local batches=$((SPLIT / 1000)) pid w c
	flock -o "$LOCK" nice -n 19 "$PB" write --run "$RUN" --phase load-a --writers "$WRITERS" >"$BASE/out/write-load-a.json" 2>"$BASE/out/write-load-a.log" &
	pid=$!
	wait_acks load-a $((batches / 5 > 0 ? batches / 5 : 1)) "$pid"
	w=$(next_second)
	a7cut A7w0 "$w"
	sleep_until $((w + 500))
	a7cut A7h0 $((w + 500))
	a7cut A7u "$(next_second)"
	kill -0 "$pid" 2>/dev/null || log "a7: the writer ended before A7u's restore began"
	pb restore --run "$RUN" --cut A7u --name paa7u >"$BASE/out/restore-paa7u.json" 2>"$BASE/out/restore-paa7u.log" || true
	log "a7: A7u restored during the load: $(jq -c '{outcome,status,seconds}' "$BASE/out/restore-paa7u.json") (writer still running: $(kill -0 "$pid" 2>/dev/null && echo yes || echo no))"
	wait "$pid"
	cut C2 load-a
	a7_ledger_cuts
	cmd_audit "$BASE/s3/src-root" src || true
	snapcuts
	# shellcheck disable=SC2046 # segargs is a word list
	for c in $(jq -r 'select(.name | test("^(C0|C2|S[0-9]+|A7.*)$")) | .name' "$RUN/cuts.jsonl"); do
		if [[ $c != A7u ]]; then
			restore_verify "$c" "pa${c,,}" path-a $(segargs "$c") || true
		elif [[ $(jq -r .outcome "$BASE/out/restore-paa7u.json") == restored ]]; then
			verify_db A7u paa7u path-a $(segargs A7u) || true
		fi
	done
	a7_report
}

# a7_ledger_cuts: cuts timed from load-a's ledger, over the middle 80% of its acknowledgements. A7wN/A7hN (A7_SECONDS
# pairs): the whole second right after an acknowledgement received in its last 300 ms, and +500 ms. A7cpre/A7cpost:
# 1 ms before the first send and 1 ms after the acknowledgement of the median batch (its commit lies between). A7m: the
# midpoint of the second holding the most acknowledgements, when it holds at least two (several commits in a second).
a7_ledger_cuts() {
	local l=$RUN/ledger/load-a.jsonl lo hi k=0 w b s a m
	read -r lo hi < <(jq -rs '[.[] | select(.ev=="ack") | .t] | sort | "\(.[length / 10 | floor]) \(.[length * 9 / 10 | floor])"' "$l")
	for w in $(jq -rs --argjson lo "$lo" --argjson hi "$hi" '[.[] | select(.ev=="ack" and .t >= $lo and .t <= $hi) | .t | select(. % 1000 >= 700) | (. / 1000 | floor + 1) * 1000] | unique | .[]' "$l" | head -n "${A7_SECONDS:-3}"); do
		k=$((k + 1))
		a7cut "A7w$k" "$w"
		a7cut "A7h$k" $((w + 500))
	done
	((k > 0)) || log "a7: no mid-load acknowledgement in a second's last 300 ms: no A7wN cuts"
	read -r b s a < <(jq -rs '[group_by(.b)[] | {b: .[0].b, s: (map(select(.ev=="start").t) | min), a: (map(select(.ev=="ack").t) | first)} | select(.a != null)] | sort_by(.a) | .[length / 2 | floor] | "\(.b) \(.s) \(.a)"' "$l")
	log "a7: load-a batch $b first sent at $s, acknowledged at $a"
	a7cut A7cpre $((s - 1))
	a7cut A7cpost $((a + 1))
	m=$(jq -rs '[.[] | select(.ev=="ack") | .t] | sort | group_by(. / 1000 | floor) | max_by(length) | if length >= 2 then "\((.[0] + .[-1]) / 2 | floor) \(length)" else empty end' "$l")
	if [[ -n $m ]]; then
		log "a7: the busiest second holds ${m#* } acknowledgements"
		a7cut A7m "${m% *}"
	else
		log "a7: no second holds two acknowledgements: the several-commits probe is NOT COVERED"
	fi
}

# a7_report: out/a7-probe.jsonl, one line per cut (T, outcome, verify, A-7 counts, verdict), and out/a7-clock.json, the
# node-vs-host clock offset bound over the run's samples. FAIL: a 201 whose verification failed, a failed restore, or a
# refusal of a required cut. Fails when any cut fails.
a7_report() {
	local out=$BASE/out/a7-probe.jsonl c db must v
	: >"$out"
	for c in $(jq -r 'select(.name | test("^(C0|C2|S[0-9]+|A7.*)$")) | .name' "$RUN/cuts.jsonl"); do
		db=pa${c,,} must=false
		required "$c" && must=true
		v=$(jq -c . "$BASE/out/verify-$db-path-a.json" 2>/dev/null) || v=null
		jq -nc --arg c "$c" --argjson at "$(cutjson "$c" .at_ms)" --argjson r "$(jq -c . "$BASE/out/restore-$db.json" 2>/dev/null || echo null)" \
			--argjson v "${v:-null}" --argjson must "$must" '($r.outcome // "failed") as $o | {cut: $c, t_ms: $at, ms_into_second: ($at % 1000),
			outcome: $o, must_restore: $must, verified: $v.passed, a7: $v.a7, problems: $v.problems,
			verdict: (if $o == "restored" then (if $v.passed == true then "pass" else "FAIL" end)
				elif ($o | startswith("refused:")) then (if $must then "FAIL" else "refused" end) else "FAIL" end)}' >>"$out"
	done
	jq -s '[.[] | .clock | select(.)] | {samples: length, offset_lo_ms: (map(.lo_ms) | max), offset_hi_ms: (map(.hi_ms) | min),
		rollbacks: (map(select(.rollback)) | length), skew_ms_used: '"${SKEW_MS:-0}"'}' "$RUN/evidence/samples-source.jsonl" >"$BASE/out/a7-clock.json" || true
	log "a7: clock $(jq -c . "$BASE/out/a7-clock.json" 2>/dev/null)"
	log "a7: $(jq -sc 'group_by(.verdict) | map({(.[0].verdict): map(.cut)}) | add' "$out")"
	local rc=0
	restore_summary || rc=1
	jq -e 'select(.verdict == "FAIL")' "$out" >/dev/null && rc=1
	return $rc
}

# required CUT: whether CUT is in REQUIRED (glob patterns, matched deliberately).
required() {
	local p pats
	read -ra pats <<<"$REQUIRED"
	# shellcheck disable=SC2053
	for p in "${pats[@]}"; do [[ $1 == $p ]] && return 0; done
	return 1
}

# snapcuts: a cut S<k> at each snapshot time of the source's audit: a bounded cut of the first phase whose ledger does
# not end before that time (nothing of it may be in the snapshot but what was in flight), the phases before it applied.
snapcuts() {
	local k=0 t p applied last
	for t in $(jq -r '[.generations[].snapshots[].at_ms] | sort | .[]' "$BASE/out/audit-src-db.json"); do
		k=$((k + 1)) applied=""
		[[ -n $(cutjson "S$k" .name) ]] && continue
		for p in load-a load-b u1 d1 r1; do
			last=$(jq -s 'map(.t) | max // 0' "$RUN/ledger/$p.jsonl" 2>/dev/null) || last=0
			((last >= t)) && break
			applied+=${applied:+,}$p
		done
		if ((last < t)); then
			log "snapcuts: the snapshot at $t follows every phase: S$k NOT TAKEN (no bounded cut expresses it)"
			continue
		fi
		pb cut --run "$RUN" --name "S$k" --applied "$applied" --partial "$p" --at-ms "$t" >"$BASE/out/cut-S$k.json"
		log "snapcuts: S$k at snapshot time $t (applied ${applied:-none}, partial $p)"
	done
}

# restore_summary: every restore of the run (out/restore-*.json) against REQUIRED: required successes (each required
# cut restored, every restore of it 201 and every verification of it passed; a pattern no cut matches counts as
# failed), refusals expected (a cut outside REQUIRED) or unexpected (a required cut), failures (no 201 and no typed
# refusal, or a 201 that failed verification), and the fault cases (out/fault-matrix.jsonl) apart, judged there. In
# qualification mode the run is GREEN only with every required success, no failure and refusals under half of the
# restores; otherwise RED, and this fails. Exploratory runs are never qualification evidence. Writes
# out/restores-summary.json.
restore_summary() {
	local f g name v p pats faults
	for f in "$BASE"/out/restore-*.json; do
		[[ -e $f ]] || continue
		name=${f##*/restore-} name=${name%.json} v=null
		for g in "$BASE"/out/verify-"$name"-*.json; do
			[[ -e $g ]] || continue
			p=$(jq -r '.passed == true' "$g" 2>/dev/null) || p=false
			[[ $v == false ]] || v=$p
		done
		jq -c --arg n "$name" --argjson v "$v" '{name: $n, cut, outcome: (.outcome // "failed"), verified: $v}' "$f" 2>/dev/null ||
			jq -nc --arg n "$name" '{name: $n, cut: null, outcome: "failed", verified: null}'
	done >"$BASE/out/restores.jsonl"
	pats=$(read -ra a <<<"$REQUIRED" && printf '%s\n' "${a[@]}" | jq -Rc 'select(length > 0) | "^" + gsub("\\*"; ".*") + "$"' | jq -sc .)
	faults=$(jq -sc 'map(.case | gsub("-"; ""))' "$BASE/out/fault-matrix.jsonl" 2>/dev/null) || faults='[]'
	jq -s --argjson pats "$pats" --argjson faults "$faults" --argjson cuts "$(jq -sc 'map(.name)' "$RUN/cuts.jsonl")" --arg mode "$MODE" '
		def req: (. // "") as $c | any($pats[]; . as $p | $c | test($p));
		def ok: .outcome == "restored" and .verified == true;
		def refused: .outcome | startswith("refused:");
		(map(select(.name as $n | any($faults[]; . as $f | $n | startswith($f)) | not))) as $rs
		| ([$cuts[] | select(req)]) as $rc
		| {mode: $mode, required_patterns: ($pats | length),
			required_passed: ([$rc[] | . as $c | select([$rs[] | select(.cut == $c)] | length > 0 and all(ok))] | length),
			required_total: (($rc | length) + ([$pats[] | . as $p | select(any($cuts[]; test($p)) | not)] | length)),
			required_not_taken: [$pats[] | . as $p | select(any($cuts[]; test($p)) | not)],
			restores: ($rs | length),
			refusals_expected: [$rs[] | select(refused and (.cut | req | not)) | "\(.name) \(.outcome)"],
			refusals_unexpected: [$rs[] | select(refused and (.cut | req)) | "\(.name) \(.outcome)"],
			failures: [$rs[] | select((refused | not) and (ok | not)) | "\(.name) \(.outcome) verified \(.verified)"],
			faults: [.[] | select(.name as $n | any($faults[]; . as $f | $n | startswith($f))) | "\(.name) \(.outcome) verified \(.verified)"]}
		| .verdict = (if .mode != "qualification" then "EXPLORATORY (not qualification evidence)"
			elif .required_total > 0 and .required_passed == .required_total and (.failures | length) == 0
				and ((.refusals_expected | length) + (.refusals_unexpected | length)) * 2 < .restores then "GREEN" else "RED" end)' \
		"$BASE/out/restores.jsonl" >"$BASE/out/restores-summary.json"
	log "restores: $(jq -r '"required successes \(.required_passed)/\(.required_total), refusals \(.refusals_expected | length) expected + \(.refusals_unexpected | length) unexpected, failures \(.failures | length) of \(.restores) restores, fault cases \(.faults | length); \(.mode): \(.verdict)"' "$BASE/out/restores-summary.json")"
	[[ $(jq -r .verdict "$BASE/out/restores-summary.json") != RED ]]
}

step=${1:?step}
shift
case $step in
matrix)
	cmd_build
	cmd_source
	cmd_load_a
	cmd_load_b
	cmd_mutations
	cmd_audit "$BASE/s3/src-root" src
	snapcuts
	cmd_tcuts
	cmd_path_a
	cmd_stop
	cmd_copy pristine
	cmd_copy pathb
	cmd_path_b "$BASE/copies/pathb" pathb
	cmd_rss_report
	restore_summary
	;;
# The ladder's matrix with the spend monitor running beside every writer phase (MONITOR_ARGS: the approved program's
# explicit --max-usd/--max-stored-bytes/--max-run-bytes, all validated by pitr-bench monitor). FAULTS=1 adds the fault
# matrix after the summary. The monitor is stopped with the run's node and writers at the end.
matrix-monitored)
	cmd_build
	cmd_source
	setsid nice -n 19 "$PB" monitor --run "$RUN" --lab-dll "$LAB_DLL" --base "$BASE" $MONITOR_ARGS </dev/null >"$BASE/out/monitor.out" 2>&1 &
	echo $! >"$BASE/out/monitor.pid"
	log "monitor started (pid $(cat "$BASE/out/monitor.pid")) args: ${MONITOR_ARGS:-G1 defaults}"
	cmd_load_a
	# PLAIN=1 runs load-b with no restart and no SIGKILL: current main's crash recovery rebuilds a killed
	# single node's files only from a replica peer ("no certified refill from an intact source"), so the CK
	# scenario cannot pass without a second node; the kill path is reported as a product finding instead.
	if [[ ${PLAIN:-0} == 1 ]]; then
		cmd_load_b_plain
	else
		cmd_load_b
	fi
	cmd_mutations
	cmd_audit "$BASE/s3/src-root" src
	snapcuts
	cmd_tcuts
	cmd_path_a
	cmd_stop
	if [[ ${PATHB:-1} == 1 ]]; then
		cmd_copy pristine
		cmd_copy pathb
		cmd_path_b "$BASE/copies/pathb" pathb
	fi
	if [[ ${FAULTS:-0} == 1 ]]; then
		cmd_fault_matrix
	fi
	if [[ -s $BASE/out/monitor.pid ]] && kill -0 "$(cat "$BASE/out/monitor.pid")" 2>/dev/null; then
		kill "$(cat "$BASE/out/monitor.pid")" 2>/dev/null || true
	fi
	cmd_rss_report
	restore_summary
	;;
build) cmd_build ;;
source) cmd_source ;;
load-a) cmd_load_a ;;
load-b) cmd_load_b ;;
load-b-plain) cmd_load_b_plain ;;
mutations) cmd_mutations ;;
audit) cmd_audit "$@" ;;
path-a) cmd_path_a ;;
rss-report) cmd_rss_report ;;
fault-matrix) cmd_fault_matrix ;;
tcuts) cmd_tcuts ;;
copy) cmd_copy "$@" ;;
path-b) cmd_path_b "$@" ;;
fault) cmd_fault "$@" ;;
mutate) cmd_mutate "$@" ;;
wrong-key) cmd_wrong_key "$@" ;;
defects) cmd_defects "$@" ;;
serve) cmd_serve "$@" ;;
unserve) cmd_unserve "$@" ;;
envfile) envfile "$@" ;;
cut) cut "$@" ;;
stop) cmd_stop ;;
restore) restore_verify "$@" ;;
summary) restore_summary ;;
p3) cmd_p3 "$@" ;;
a7)
	trap 'cmd_stop; rss_max source' EXIT
	cmd_build
	cmd_source
	cmd_a7
	;;
*) echo "unknown step $step" >&2; exit 2 ;;
esac
