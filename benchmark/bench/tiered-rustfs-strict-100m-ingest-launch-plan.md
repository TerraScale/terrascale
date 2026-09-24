# TerraBase strict 100M RustFS ingest launch plan

Status: prepared, not launched.

This plan is for the strict ingest-only capacity run. It must remain blocked
until the current-source 10M ingest calibration has completed and its frozen
Release assembly SHA-256 has been inserted into `BENCH_DLL` and
`BENCH_SHA256` below. This run does not claim scans, queries, mutations,
compaction, backup, or restore.

No 100M run has been launched or qualified. Any prior 10M measurement cited by
this plan is historical planning evidence until a current-source calibration
passes the gate below.

## Run envelope

| Control | Value |
| --- | ---: |
| Records | `100,000,000` |
| Batch | `65,536` |
| Payload | `128` bytes, `per-key-incompressible` |
| Updates / deletes / point reads | `0 / 0 / 0` |
| Checkpoint interval | `500,000` records |
| Segment cache budget | `16 MiB` |
| Local engine cap | `85,899,345,920` bytes (`80 GiB`) |
| Filesystem free-space floor | `107,374,182,400` bytes (`100 GiB`) |
| Required preflight free space | `268,435,456,000` bytes (`250 GiB`) |
| Cgroup memory | `4 GiB` |
| Cgroup swap | `0` |
| Cgroup OOM policy | `stop` |
| CPU priority | `nice -n 19` |
| Runtime limit | `12h` |
| RustFS endpoint | `http://127.0.0.1:19000` |
| RustFS bucket | `terrabase-tiered` |

The benchmark's local storage measurement covers its output engine root. The
free-space guard also covers the RustFS data root because both are on the
host filesystem. RustFS credentials are loaded from the existing private file
and are never printed or written into launch metadata.

The historical prior strict 10M ingest measured approximately 686 B of local engine
growth and 422 B of RustFS journal growth per requested record. A linear
100M estimate is therefore approximately 68.6 GB local, 42.2 GB remote, and
110.8 GB combined additional filesystem use. These are planning estimates;
the current-source 10M calibration must establish the actual slope and bounded
frontier-validation memory before launch.

## Parameterized strict launch

Use this block first for the current-source 10M calibration. The 100M run is
allowed only after the calibration gate below passes. Both runs must use the
same frozen Release DLL, assembly SHA-256, source commit, tree digest, dirty
working-tree digest, and .NET runtime. Set those values only after the source
test suite and artifact freeze finish. Keep this shell session available for
the monitor or source `launch.env` from another terminal.

```bash
set -Eeuo pipefail

BENCH_DLL="${BENCH_DLL:?Set BENCH_DLL to the frozen Release TerraBase.Benchmarks.dll}"
BENCH_SHA256="${BENCH_SHA256:?Set BENCH_SHA256 to the frozen assembly SHA-256}"
SOURCE_COMMIT="${SOURCE_COMMIT:?Set SOURCE_COMMIT to the frozen source commit}"
SOURCE_TREE_DIGEST="${SOURCE_TREE_DIGEST:?Set SOURCE_TREE_DIGEST to the frozen source tree digest}"
SOURCE_WORKTREE_DIGEST="${SOURCE_WORKTREE_DIGEST:?Set SOURCE_WORKTREE_DIGEST to the frozen working-tree digest}"
SOURCE_DIRTY="${SOURCE_DIRTY:?Set SOURCE_DIRTY to true or false for the frozen source state}"
EXPECTED_RECORDS="${EXPECTED_RECORDS:?Set EXPECTED_RECORDS to 10000000 for calibration or 100000000 for qualification}"
RUN_LABEL="${RUN_LABEL:?Set RUN_LABEL to a unique tiered-rustfs run label}"
DOTNET_BIN="${DOTNET_BIN:-/home/mariogk/.local/share/mise/dotnet-root/dotnet}"

BASE_DIR=/home/mariogk/Projects/TerraScale/ts-benchmark/bench
RUN_ID="${RUN_LABEL}-$(date -u +%Y%m%dT%H%M%S%NZ)"
OUTPUT="$BASE_DIR/receipts/$RUN_ID"
PREFIX="$RUN_ID"
UNIT="terrabase-${RUN_LABEL#tiered-rustfs-strict-}-${RUN_ID##*-}.service"

test -f "$BENCH_DLL"
test "$(sha256sum "$BENCH_DLL" | awk '{print $1}')" = "$BENCH_SHA256"
test -x "$DOTNET_BIN"
[[ "$BENCH_SHA256" =~ ^[0-9a-fA-F]{64}$ ]]
[[ "$SOURCE_COMMIT" =~ ^[0-9a-fA-F]{40}$ ]]
[[ "$SOURCE_TREE_DIGEST" =~ ^[0-9a-fA-F]{40}$ ]]
[[ "$SOURCE_WORKTREE_DIGEST" =~ ^[0-9a-fA-F]{64}$ ]]
[[ "$SOURCE_DIRTY" == true || "$SOURCE_DIRTY" == false ]]
[[ "$EXPECTED_RECORDS" == 10000000 || "$EXPECTED_RECORDS" == 100000000 ]]
[[ "$RUN_LABEL" != *'/'* && "$RUN_LABEL" != *' '* ]]
test ! -e "$OUTPUT"

AVAILABLE_BYTES="$(df -B1 --output=avail "$BASE_DIR/receipts" | awk 'NR == 2 { print $1 }')"
test "$AVAILABLE_BYTES" -ge 268435456000

test "$(podman inspect --format '{{.State.Status}}' terrabase-rustfs-20260922 2>/dev/null)" = running
test "$(systemctl --user is-active "$UNIT" 2>/dev/null || true)" != active
test -r /run/user/1000/terrabase-rustfs-credentials.sh

mkdir "$OUTPUT"
LAUNCH_EPOCH="$(date +%s)"
{
    printf 'RUN_ID=%q\n' "$RUN_ID"
    printf 'OUTPUT=%q\n' "$OUTPUT"
    printf 'PREFIX=%q\n' "$PREFIX"
    printf 'UNIT=%q\n' "$UNIT"
    printf 'BENCH_DLL=%q\n' "$BENCH_DLL"
    printf 'BENCH_SHA256=%q\n' "$BENCH_SHA256"
    printf 'SOURCE_COMMIT=%q\n' "$SOURCE_COMMIT"
    printf 'SOURCE_TREE_DIGEST=%q\n' "$SOURCE_TREE_DIGEST"
    printf 'SOURCE_WORKTREE_DIGEST=%q\n' "$SOURCE_WORKTREE_DIGEST"
    printf 'SOURCE_DIRTY=%q\n' "$SOURCE_DIRTY"
    printf 'EXPECTED_RECORDS=%q\n' "$EXPECTED_RECORDS"
    printf 'RUN_LABEL=%q\n' "$RUN_LABEL"
    printf 'DOTNET_BIN=%q\n' "$DOTNET_BIN"
    printf 'LAUNCH_EPOCH=%q\n' "$LAUNCH_EPOCH"
} > "$OUTPUT/launch.env"

systemd-run --user --unit="$UNIT" --collect --quiet \
    --property=MemoryMax=4G \
    --property=MemorySwapMax=0 \
    --property=OOMPolicy=stop \
    --property=RuntimeMaxSec=12h \
    --property=WorkingDirectory="$BASE_DIR" \
    --setenv=PLAN_OUTPUT="$OUTPUT" \
    /run/current-system/sw/bin/bash -lc '
        set -Eeuo pipefail
        source /run/user/1000/terrabase-rustfs-credentials.sh
        source "$PLAN_OUTPUT/launch.env"
        export TERRABASE_S3_BUCKET=terrabase-tiered
        export TERRABASE_S3_ENDPOINT=http://127.0.0.1:19000
        exec > "$PLAN_OUTPUT/console.log" 2>&1
        exec /run/current-system/sw/bin/nice -n 19 "$DOTNET_BIN" "$BENCH_DLL" \
            --workload tiered-segment \
            --records "$EXPECTED_RECORDS" \
            --batch 65536 \
            --payload-bytes 128 \
            --payload-mode per-key-incompressible \
            --updates 0 \
            --deletes 0 \
            --point-reads 0 \
            --checkpoint-records 500000 \
            --cache-bytes 16777216 \
            --ingest-only \
            --max-local-storage-bytes 85899345920 \
            --min-free-space-bytes 107374182400 \
            --resource-sample-ms 500 \
            --require-isolated-cgroup \
            --s3-endpoint http://127.0.0.1:19000 \
            --s3-bucket terrabase-tiered \
            --prefix "$PREFIX" \
            --output "$OUTPUT"
    ' &
printf '%s\n' "$!" > "$OUTPUT/launcher.pid"
printf 'RUN_ID=%s\nOUTPUT=%s\nUNIT=%s\n' "$RUN_ID" "$OUTPUT" "$UNIT"
```

The generated prefix and output directory contain nanoseconds and are checked
for local freshness before launch. The benchmark independently rejects a
nonempty remote prefix.

### Calibration and qualification invocations

After the source test suite and Release artifact freeze, run the calibration
with the exact source and assembly identity that will be used for qualification:

```bash
export EXPECTED_RECORDS=10000000
export RUN_LABEL=tiered-rustfs-strict-10m-ingest-calibration
# Set BENCH_DLL, BENCH_SHA256, SOURCE_COMMIT, SOURCE_TREE_DIGEST,
# SOURCE_WORKTREE_DIGEST, SOURCE_DIRTY, and DOTNET_BIN to the frozen values,
# then execute the parameterized strict launch block above in this shell.
```

Use the same values after the 10M gate passes for the 100M run:

```bash
export EXPECTED_RECORDS=100000000
export RUN_LABEL=tiered-rustfs-strict-100m-ingest
# Reuse the identical frozen BENCH_* / SOURCE_* / DOTNET_BIN values, then
# execute the parameterized strict launch block above in this shell.
```

These examples show the required parameter values; execute the launch block
itself as a whole so its `set -Eeuo pipefail`, private credential sourcing,
fresh output checks, and systemd envelope remain intact.

### Measured 10M promotion gate

Promote the frozen artifact to the 100M run only when the 10M receipt proves
all of the following:

- `Status=complete`, with `RemoteAck.AcknowledgedRecords >= 10000000` and
  `RemoteValidatedFrontier == RemoteAck.AcknowledgedRecords`.
- The local WAL is nonempty, `ResourceSamplerError` is null, and the receipt
  reports no local-cap or free-space violation.
- The cgroup reports `MemoryMax=4294967296`, `SwapMax=0`, and zero `oom`,
  `oom_kill`, and `oom_group_kill` counters.
- `monitor.tsv` contains no `STOP` row and its maximum anonymous-memory sample
  is below `3758096384` bytes (3.5 GiB).
- Overall seed throughput is at least 4,000 records/s and the minimum of the
  last three full checkpoint interval throughputs is at least 3,000 records/s.
- The receipt's source commit, tree digest, working-tree digest, dirty flag,
  and benchmark assembly SHA-256 exactly match the values planned for 100M.

Measure the throughput bullets from the checkpoint telemetry with this command
after sourcing that run's `launch.env`:

```bash
python3 - "$OUTPUT/progress.jsonl" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    rows = [json.loads(line) for line in stream]
seed = [row for row in rows if row.get("phase") == "seed" and row.get("completed", 0) > 0]
if len(seed) < 4:
    raise SystemExit("promotion throughput gate requires at least four seed checkpoints")

step = seed[1]["completed"] - seed[0]["completed"]
full_rates = []
for previous, current in zip(seed, seed[1:]):
    records = current["completed"] - previous["completed"]
    seconds = current["elapsedSeconds"] - previous["elapsedSeconds"]
    if records == step and seconds > 0:
        full_rates.append(records / seconds)

overall = seed[-1]["completed"] / seed[-1]["elapsedSeconds"]
last_three = full_rates[-3:]
print(f"overall_records_per_second={overall:.3f}")
print("last_three_full_checkpoint_records_per_second=" + ",".join(f"{rate:.3f}" for rate in last_three))
if overall < 4000 or len(last_three) < 3 or min(last_three) < 3000:
    raise SystemExit("promotion throughput gate failed")
PY
```

The recorded historical prior 10M receipt measured 4,845 records/s overall and a minimum
of 3,449 records/s over its last three full checkpoint intervals. It has no
`monitor.tsv`, and its assembly SHA-256 is from the earlier frozen artifact,
so it is throughput evidence only; it does not pass this promotion gate. A
current-source calibration with the final frozen artifact must run before 100M.
This ingest-only gate does not qualify CRUD, scans, backup, restore, or
compaction.

## Monitor and stop policy

Run this in a second terminal after sourcing the launch metadata. It records
measurements in `monitor.tsv` and stops the unit when a hard safety condition
is reached. It does not delete the output directory or RustFS objects.

The fixture updates `tiered-rustfs-receipt.json` and `progress.jsonl` from
`WriteCheckpointAsync` at each checkpoint. In the prior strict 10M run,
`--checkpoint-records 500000` produced 20 seed checkpoints at completed values
`524288, 1048576, ..., 9961472, 10000000`; the observed seed intervals were
about 72 to 152 seconds after the initial warmup. The monitor samples the
receipt every minute, so its `completed` column advances during seed ingest and
the 60 minute no-progress allowance covers the expected remote-frontier
validation phase after the final seed checkpoint.

```bash
set -Eeuo pipefail
source /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/<RUN_ID>/launch.env

OUTPUT="$OUTPUT"
RECEIPT="$OUTPUT/tiered-rustfs-receipt.json"
PROGRESS="$OUTPUT/progress.jsonl"
MONITOR_LOG="$OUTPUT/monitor.tsv"
LOCAL_CAP=85899345920
FREE_FLOOR=107374182400
NO_PROGRESS_SECONDS=3600
ANON_STOP=3758096384
ANON_STOP_SECONDS=600
PRESSURE_ANON=2147483648
PRESSURE_FULL_LIMIT=10.0
PRESSURE_STALL_SECONDS=600
WALL_SECONDS=43200

printf 'utc\tstate\tphase\tcompleted\tfree\tlocal\tmemory_current\tmemory_peak\tanon\tfile\tpsi_some_avg10\tpsi_full_avg10\toom\toom_kill\toom_group_kill\n' > "$MONITOR_LOG"
last_progress_mtime=0
last_progress_epoch="$(date +%s)"
anon_high_since=0

read_event() {
    awk -v key="$1" '$1 == key { print $2; found = 1 } END { if (!found) print 0 }' "$2"
}

read_stat() {
    awk -v key="$1" '$1 == key { print $2; found = 1 } END { if (!found) print 0 }' "$2"
}

read_psi_avg10() {
    awk -v key="$1" '$1 == key { for (field = 2; field <= NF; field++) if ($field ~ /^avg10=/) { value = $field; sub(/^avg10=/, "", value); print value; found = 1 } } END { if (!found) print 0 }' "$2"
}

stop_run() {
    reason="$1"
    printf '%s\tSTOP\t%s\n' "$(date -u +%FT%TZ)" "$reason" | tee -a "$MONITOR_LOG" >&2
    systemctl --user stop "$UNIT" 2>/dev/null || true
}

while :; do
    state="$(systemctl --user show "$UNIT" -p ActiveState --value 2>/dev/null || printf missing)"
    if [[ "$state" == inactive || "$state" == failed || "$state" == missing ]]; then
        break
    fi

    now="$(date +%s)"
    if (( now - LAUNCH_EPOCH >= WALL_SECONDS )); then
        stop_run '12-hour wall limit'
        break
    fi

    free_bytes="$(df -B1 --output=avail "$OUTPUT" | awk 'NR == 2 { print $1 }')"
    local_bytes=0
    if [[ -d "$OUTPUT/engine" ]]; then
        local_bytes="$(du -sB1 "$OUTPUT/engine" | awk '{ print $1 }')"
    fi
    if (( free_bytes < FREE_FLOOR )); then
        stop_run "free space ${free_bytes} below ${FREE_FLOOR}"
        break
    fi
    if (( local_bytes > LOCAL_CAP )); then
        stop_run "local engine ${local_bytes} above ${LOCAL_CAP}"
        break
    fi

    control_group="$(systemctl --user show "$UNIT" -p ControlGroup --value 2>/dev/null || true)"
    cgroup="/sys/fs/cgroup${control_group}"
    if [[ ! -d "$cgroup" ]]; then
        sleep 30
        continue
    fi
    memory_current="$(cat "$cgroup/memory.current")"
    memory_peak="$(cat "$cgroup/memory.peak")"
    anon="$(read_stat anon "$cgroup/memory.stat")"
    file_bytes="$(read_stat file "$cgroup/memory.stat")"
    psi_some_avg10="$(read_psi_avg10 some "$cgroup/memory.pressure")"
    psi_full_avg10="$(read_psi_avg10 full "$cgroup/memory.pressure")"
    oom="$(read_event oom "$cgroup/memory.events")"
    oom_kill="$(read_event oom_kill "$cgroup/memory.events")"
    oom_group_kill="$(read_event oom_group_kill "$cgroup/memory.events")"
    if (( oom > 0 || oom_kill > 0 || oom_group_kill > 0 )); then
        stop_run "OOM counters oom=${oom} oom_kill=${oom_kill} oom_group_kill=${oom_group_kill}"
        break
    fi

    if (( anon >= ANON_STOP )); then
        (( anon_high_since == 0 )) && anon_high_since="$now"
        if (( now - anon_high_since >= ANON_STOP_SECONDS )); then
            stop_run "anonymous memory remained at least 3.5 GiB for 10 minutes"
            break
        fi
    else
        anon_high_since=0
    fi

    progress_mtime=0
    [[ -f "$PROGRESS" ]] && progress_mtime="$(stat -c %Y "$PROGRESS")"
    if (( progress_mtime > last_progress_mtime )); then
        last_progress_mtime="$progress_mtime"
        last_progress_epoch="$now"
    elif (( now - last_progress_epoch >= NO_PROGRESS_SECONDS )); then
        stop_run 'no progress file update for 60 minutes'
        break
    fi

    if awk -v psi="$psi_full_avg10" -v anon="$anon" -v pressure="$PRESSURE_FULL_LIMIT" -v anon_floor="$PRESSURE_ANON" 'BEGIN { exit !(psi >= pressure && anon >= anon_floor) }' \
        && (( now - last_progress_epoch >= PRESSURE_STALL_SECONDS )); then
        stop_run "full memory PSI avg10=${psi_full_avg10}% with anonymous memory ${anon} and stalled progress"
        break
    fi

    phase='starting'
    completed=0
    if [[ -s "$RECEIPT" ]]; then
        phase="$(jq -r '.Phase // "unknown"' "$RECEIPT" 2>/dev/null || printf unknown)"
        completed="$(jq -r '.CompletedRecords // 0' "$RECEIPT" 2>/dev/null || printf 0)"
    fi
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$(date -u +%FT%TZ)" "$state" "$phase" "$completed" "$free_bytes" \
        "$local_bytes" "$memory_current" "$memory_peak" "$anon" "$file_bytes" \
        "$psi_some_avg10" "$psi_full_avg10" "$oom" "$oom_kill" "$oom_group_kill" \
        >> "$MONITOR_LOG"
    sleep 60
done
```

Hard stop conditions are free space below 100 GiB, local engine usage above
80 GiB, any OOM counter, anonymous memory at or above 3.5 GiB for ten minutes,
full memory PSI at or above 10% with at least 2 GiB anonymous memory and ten
minutes of stalled progress, no progress update for sixty minutes, or the
12-hour runtime limit. `memory.current` is recorded but is not a stop signal
by itself because the prior strict 10M run reached 4,259,565,568 B with about
3.98 GiB inactive file cache, only about 0.24 GiB anonymous memory, and zero
OOM events. Reclaim `max` events alone are also recorded without stopping.

## Completion verification

Run this after the unit becomes inactive. A successful exit code is not enough;
the receipt must satisfy the durability and resource assertions below.

```bash
set -Eeuo pipefail
source /home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/<RUN_ID>/launch.env
RECEIPT="$OUTPUT/tiered-rustfs-receipt.json"
MONITOR_LOG="$OUTPUT/monitor.tsv"

test -s "$RECEIPT"
test -s "$OUTPUT/tiered-rustfs-summary.md"
test -s "$OUTPUT/resource-envelope.json"
test -s "$OUTPUT/resource-samples.jsonl"
test -s "$OUTPUT/progress.jsonl"
test -s "$OUTPUT/engine/wal"
test -s "$MONITOR_LOG"

! awk -F '\t' 'NR > 1 && $2 == "STOP" { found = 1 } END { exit found }' "$MONITOR_LOG"
awk -F '\t' 'NR > 1 && $9 ~ /^[0-9]+$/ { seen = 1; if ($9 >= 3758096384) found = 1 } END { exit (!seen || found) }' "$MONITOR_LOG"

jq -e \
    --arg expected_sha "$BENCH_SHA256" \
    --argjson expected_records "$EXPECTED_RECORDS" \
    --arg source_commit "$SOURCE_COMMIT" \
    --arg source_tree_digest "$SOURCE_TREE_DIGEST" \
    --arg source_worktree_digest "$SOURCE_WORKTREE_DIGEST" \
    --argjson source_dirty "$SOURCE_DIRTY" \
    '
    .Status == "complete"
    and .Phase == "complete"
    and .Error == null
    and .TargetRecords == $expected_records
    and .CompletedRecords == $expected_records
    and .Subject.Commit == $source_commit
    and .Subject.TreeDigest == $source_tree_digest
    and .Subject.WorkingTreeDigest == $source_worktree_digest
    and .Subject.Dirty == $source_dirty
    and .IngestOnly == true
    and .RequireIsolatedCgroup == true
    and .IsolatedCgroup == true
    and .BenchmarkAssemblySha256 == $expected_sha
    and .MaxLocalStorageBytes == 85899345920
    and .MinimumFreeSpaceBytes == 107374182400
    and .PeakLocalStorageBytes <= 85899345920
    and .MinimumObservedFreeSpaceBytes >= 107374182400
    and .LocalStorageLimitExceededBytes == null
    and .FreeSpaceLimitViolation == null
    and .ResourceSamplerError == null
    and .LocalWalBytes > 0
    and .RemoteObjects.ObjectCount == 0
    and .RemoteObjects.TotalBytes == 0
    and .RemoteJournalObjects.ObjectCount > 0
    and .RemoteJournalObjects.TotalBytes > 0
    and .RemoteAck.AcknowledgedRecords >= $expected_records
    and .RemoteValidatedFrontier == .RemoteAck.AcknowledgedRecords
    and .FinalCgroup.MemoryMax == "4294967296"
    and .FinalCgroup.SwapMax == "0"
    and (.FinalCgroup.MemoryEvents | test("(^|\\n)oom 0(\\n|$)"))
    and (.FinalCgroup.MemoryEvents | test("(^|\\n)oom_kill 0(\\n|$)"))
    and (.FinalCgroup.MemoryEvents | test("(^|\\n)oom_group_kill 0(\\n|$)"))
    and ((.SkippedPhases | index("indexed-queries")) != null)
    and ((.SkippedPhases | index("backup")) != null)
    and ((.SkippedPhases | index("restore")) != null)
' "$RECEIPT" > "$OUTPUT/receipt-verification.json"

sha256sum "$BENCH_DLL" "$RECEIPT" "$OUTPUT/tiered-rustfs-summary.md" \
    > "$OUTPUT/verified-sha256sums.txt"
jq '{status:.Status,phase:.Phase,target:.TargetRecords,completed:.CompletedRecords,remoteAck:.RemoteAck,remoteFrontier:.RemoteValidatedFrontier,remoteJournal:.RemoteJournalObjects,peakLocal:.PeakLocalStorageBytes,minimumFree:.MinimumObservedFreeSpaceBytes,finalCgroup:.FinalCgroup}' \
    "$RECEIPT"
```

Preserve failed receipts, `console.log`, `monitor.tsv`, and any RustFS prefix
for diagnosis. Do not reuse a prefix or output directory for a rerun. The
receipt's remote frontier is the required proof that every acknowledged batch
was present in the remote commit journal; this ingest-only plan does not prove
the later CRUD, query, backup, restore, or compaction functions.
