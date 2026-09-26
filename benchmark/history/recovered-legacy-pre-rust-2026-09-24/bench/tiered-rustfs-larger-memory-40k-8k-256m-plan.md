# RustFS tiered larger-than-memory qualification: 40K × 8 KiB

Status: prepared, not launched.

This plan qualifies the direct tiered `StorageEngine` path with RustFS as the
immutable object tier and a deliberately small SSD cache. It is separate from
the existing local-only `larger-memory` workload and from the ingest-only
10M/100M runs.

The run must wait for the active
`terrabase-e2e-rustfs-1m-final-20260923T215722Z.service` to exit. Its current
source-matched binary is still the known frozen artifact below; replace the
artifact identity only after a new source-matched Release freeze.

## Qualification contract

| Control | Value |
| --- | ---: |
| Records | `40,000` |
| Payload | `8,192` bytes, `per-key-incompressible` |
| Minimum logical payload | `327,680,000` bytes (`40,000 × 8,192`) |
| Cgroup memory | `268,435,456` bytes (`256 MiB`) |
| Cgroup swap | `0` |
| OOM policy | `stop` |
| Write batch | `64` records |
| Updates / deletes | `100 / 1,000` |
| Point reads | `32` |
| Exact scan checkpoint | `4,096` records |
| Idle SSD cache budget | `4 MiB` |
| Maximum local engine bytes | `4 GiB` |
| Free-space floor | `100 GiB` |
| S3 endpoint | `http://127.0.0.1:19000` |
| S3 provider | RustFS container `terrabase-rustfs-20260922` |
| S3 bucket | `terrabase-tiered` |
| CPU priority | `nice -n 19` |
| Runtime limit | `4h` |

The 64-record batch keeps each remote commit frame near 1 MiB for this
payload, far below the 128 MiB remote-frame and segment bounds. It also keeps
the mutation and WAL admission lists small under the 256 MiB cgroup. The
qualification is correctness-first; it does not represent a throughput target.

The `--cache-bytes 4194304` value is an idle cache budget. The implementation
documents that an active read lease, a segment upload/download, and `.tmp`
staging need additional SSD. The gate therefore checks both the idle budget
setting and the observed active-cache/staging envelope. A result that exceeds
those physical bounds fails qualification even if the process exits normally.

## Covered phases

The command intentionally omits `--ingest-only`. The existing tiered workload
then performs all of these checks:

1. Remote journal publication before each acknowledged WAL append during seed,
   followed by a complete local-root loss and journal WAL rebuild.
2. Exact seed scan and point reads after that root loss.
3. Initial full compaction, remote segment publication, object listing, and
   explicit SSD cache eviction.
4. Cold remote reopen and exact scan after cache eviction.
5. `100` updates and `1,000` deletes, including absent and surviving body
   verification.
6. A second complete local-root loss after the mutations, exact state
   verification, and final compaction.
7. Final cache eviction, cold remote reopen, final exact scan of `39,000`
   surviving records, and final point reads.

The direct tiered workload provides point reads and collection scans. Indexed
query planning, backup, restore, and HTTP listing remain covered by the
separate E2E RustFS workload; this run must not be described as covering those
surfaces.

## Frozen artifact and local topology

Known source-matched Release identity at plan time:

```text
binary root: /home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/terrabase-final-source-matched-release-20260923T215453Z
benchmark SHA-256: 9a8567448833276d091a459a65f0a70a0c5be114dc24395251ce9d49a1b9d913
source commit: 67e35f443565c54eb731d6ea2dd989cb5c87b7be
source tree digest: 69203400f13c7e311cf5fb569a081eeab36e8cbc
source working-tree digest: 5c5a337f2fdf5fec04557248f69f3f1cbf031ad0a22fff19b7a67062aac5db3a
dotnet: 11.0.100-rc.1.26425.128
```

The binary must be verified from its `freeze-manifest.txt` and assembly hash.
The benchmark captures source identity again in `run-receipt.json`; do not
hand-edit either receipt.

The read-only CLI check used the real frozen binary in a 256 MiB, zero-swap
systemd cgroup:

```bash
systemd-run --user --wait --pipe --quiet \
  --unit=terrabase-tiered-cli-help-check \
  --property=MemoryMax=268435456 --property=MemorySwapMax=0 \
  --property=OOMPolicy=stop \
  --working-directory=/home/mariogk/Projects/TerraScale/ts-benchmark \
  /home/mariogk/.local/share/mise/dotnet-root/dotnet \
  /home/mariogk/Projects/TerraScale/ts-benchmark/bench/binaries/terrabase-final-source-matched-release-20260923T215453Z/TerraBase.Benchmarks.dll \
  --workload tiered-segment --help
```

It exited `0` and printed the tiered option line. The captured temporary log
was SHA-256
`9812be0a189df9ff1b1b3e6f1d80e99450b499b2e5e2411d9573cd8ecfff98ef`.
No workload or RustFS object was created by this check.

## Reproducible launcher and verifier

Use
[`strict-rustfs-tiered-40k.sh`](commands/strict-rustfs-tiered-40k.sh) for the
qualification. It accepts only a frozen source-matched Release manifest,
creates a fresh run ID and S3 prefix, and keeps all artifacts below
`bench/receipts`. Preflight never creates a receipt or starts a process:

```bash
./bench/commands/strict-rustfs-tiered-40k.sh preflight \
  --manifest bench/binaries/<selected-final-source-matched-release>/freeze-manifest.txt
```

After preflight reports a quiet host, launch the finite systemd service:

```bash
./bench/commands/strict-rustfs-tiered-40k.sh launch \
  --manifest bench/binaries/<selected-final-source-matched-release>/freeze-manifest.txt
```

The launcher sources RustFS credentials only inside the service, applies a
256 MiB memory limit with zero swap and `OOMPolicy=stop`, runs at `nice -n 19`,
and records the exact binary/source identity beside the output. Wait for the
reported unit to become inactive, then run the verifier with the reported run
directory:

```bash
./bench/commands/strict-rustfs-tiered-40k.sh verify \
  --run-dir bench/receipts/<run-id>
```

Verification fails unless the receipt proves both root-loss drills, exact
seed and final scans, updates and deletes, initial and final compaction,
cache eviction followed by cold remote reopen scans, remote journal
acknowledgements, zero swap/OOM counters, and the configured local/cache/
staging/free-space bounds. Do not start the launcher while the active 1M
service is running; the preflight checks this condition and the shared RustFS
backing disk.

## Launch command

Run this only after the active 1M service is inactive and the selected binary
has passed its source and assembly hash checks. It writes all run artifacts
under `../ts-benchmark/bench/receipts`; credentials are sourced only inside
the isolated service and are not copied into metadata.

```bash
set -Eeuo pipefail

TB_BENCH_ROOT=/home/mariogk/Projects/TerraScale/ts-benchmark
TB_DOTNET=/home/mariogk/.local/share/mise/dotnet-root/dotnet
TB_BENCH_DLL="$TB_BENCH_ROOT/bench/binaries/terrabase-final-source-matched-release-20260923T215453Z/TerraBase.Benchmarks.dll"
TB_EXPECTED_BENCH_SHA256=9a8567448833276d091a459a65f0a70a0c5be114dc24395251ce9d49a1b9d913
TB_CREDENTIALS=/run/user/1000/terrabase-rustfs-credentials.sh
TB_RUN_ID="tiered-rustfs-40k-8k-256m-$(date -u +%Y%m%dT%H%M%SZ)"
TB_OUTPUT="$TB_BENCH_ROOT/bench/receipts/$TB_RUN_ID"
TB_PREFIX="$TB_RUN_ID"
TB_UNIT="terrabase-$TB_RUN_ID.service"

test -x "$TB_DOTNET"
test -f "$TB_BENCH_DLL"
test "$(sha256sum "$TB_BENCH_DLL" | awk '{print $1}')" = "$TB_EXPECTED_BENCH_SHA256"
test -r "$TB_CREDENTIALS"
test "$(podman inspect --format '{{.State.Status}}' terrabase-rustfs-20260922)" = running
test "$(systemctl --user is-active terrabase-e2e-rustfs-1m-final-20260923T215722Z.service 2>/dev/null || true)" != active
test ! -e "$TB_OUTPUT"
test "$(df -B1 --output=avail "$TB_BENCH_ROOT/bench/receipts" | awk 'NR == 2 {print $1}')" -ge 107374182400
mkdir "$TB_OUTPUT"

systemd-run --user --wait --collect --quiet \
  --unit="$TB_UNIT" \
  --property=MemoryMax=268435456 \
  --property=MemorySwapMax=0 \
  --property=OOMPolicy=stop \
  --property=RuntimeMaxSec=4h \
  --working-directory="$TB_BENCH_ROOT" \
  --setenv=TB_RUN_OUTPUT="$TB_OUTPUT" \
  --setenv=TB_BENCH_DLL="$TB_BENCH_DLL" \
  --setenv=TB_DOTNET="$TB_DOTNET" \
  --setenv=TB_PREFIX="$TB_PREFIX" \
  /run/current-system/sw/bin/bash -lc '
    set -Eeuo pipefail
    source /run/user/1000/terrabase-rustfs-credentials.sh
    export TERRABASE_S3_ENDPOINT=http://127.0.0.1:19000
    export TERRABASE_S3_BUCKET=terrabase-tiered
    export TERRABASE_S3_REGION=us-east-1
    exec > "$TB_RUN_OUTPUT/console.log" 2>&1
    exec nice -n 19 "$TB_DOTNET" "$TB_BENCH_DLL" \
      --workload tiered-segment \
      --records 40000 \
      --batch 64 \
      --payload-bytes 8192 \
      --payload-mode per-key-incompressible \
      --updates 100 \
      --deletes 1000 \
      --point-reads 32 \
      --checkpoint-records 4096 \
      --cache-bytes 4194304 \
      --max-local-storage-bytes 4294967296 \
      --min-free-space-bytes 107374182400 \
      --resource-sample-ms 500 \
      --require-isolated-cgroup \
      --s3-endpoint http://127.0.0.1:19000 \
      --s3-bucket terrabase-tiered \
      --s3-region us-east-1 \
      --prefix "$TB_PREFIX" \
      --output "$TB_RUN_OUTPUT"
  '

systemctl --user show "$TB_UNIT" \
  -p Id -p ActiveState -p Result -p ExecMainStatus -p MemoryMax \
  -p MemoryPeak -p MemorySwapMax -p OOMPolicy > "$TB_OUTPUT/systemd-status.txt"
```

## Qualification gate

The launch is qualified only if the process exits `0` and this check passes.
The remote acknowledgement lower bound is `40,000 + 100 + 1,000 + 4 = 41,104`
physical records; the extra four records are the engine's identity records
observed in the existing strict tiered receipts.

```bash
set -Eeuo pipefail
TB_RECEIPT="$TB_OUTPUT/tiered-rustfs-receipt.json"
TB_SUMMARY="$TB_OUTPUT/tiered-rustfs-summary.md"
test -s "$TB_RECEIPT"
test -s "$TB_SUMMARY"

jq -e '
  def phase($name; $records):
    any(.PhaseMetrics[]; .Name == $name and .CompletedRecords == $records);
  .Status == "complete" and
  .TargetRecords == 40000 and .CompletedRecords == 40000 and
  .PayloadBytes == 8192 and .PayloadMode == "per-key-incompressible" and
  .PayloadSeed == 1337 and .Batch == 64 and
  .RemoteAck.AcknowledgedRecords >= 41104 and
  .RemoteAck.AcknowledgedBatches > 0 and
  .RemoteObjects.ObjectCount > 0 and .RemoteObjects.TotalBytes > 0 and
  .RemoteJournalObjects.ObjectCount > 0 and .RemoteJournalObjects.TotalBytes > 0 and
  .SeedRootLossRecoverySeconds > 0 and
  .MutationRootLossRecoverySeconds > 0 and
  .ResourceSamplerError == null and
  .FreeSpaceLimitViolation == null and
  .LocalStorageLimitExceededBytes == null and
  .PeakLocalStorageBytes <= 4294967296 and
  .PeakCacheBytes <= (4194304 + 128 * 1024 * 1024) and
  .PeakStagingBytes <= (2 * 128 * 1024 * 1024) and
  .CacheBytesAfterInitialVerification == 0 and
  .CacheBytesAfterVerification == 0 and
  phase("seed-root-loss-recovery"; 40000) and
  phase("initial-compaction-upload"; 40000) and
  phase("initial-exact-scan"; 40000) and
  phase("initial-remote-reopen-scan"; 40000) and
  phase("updates"; 100) and
  phase("deletes"; 1000) and
  phase("mutation-root-loss-recovery"; 39000) and
  phase("final-compaction-upload"; 40000) and
  phase("final-exact-scan"; 39000) and
  phase("final-remote-reopen-scan"; 39000) and
  .FinalCgroup.MemoryMax == "268435456" and
  .FinalCgroup.SwapMax == "0" and
  (.FinalCgroup.MemoryPeak | tonumber) <= 268435456 and
  .FinalCgroup.SwapCurrent == "0" and
  (.FinalCgroup.MemoryEvents | test("(^|\\n)oom 0($|\\n)")) and
  (.FinalCgroup.MemoryEvents | test("(^|\\n)oom_kill 0($|\\n)")) and
  (.FinalCgroup.MemoryEvents | test("(^|\\n)oom_group_kill 0($|\\n)"))
' "$TB_RECEIPT"
```

Also retain the raw `resource-samples.jsonl`, `progress.jsonl`, cgroup
envelope, systemd status, console log, source freeze manifest, and assembly
hash. Report `PeakRssBytes`, cgroup `MemoryPeak`, `PeakLocalStorageBytes`,
`PeakCacheBytes`, `PeakStagingBytes`, remote journal/segment bytes, and phase
durations from the receipt.

## Memory feasibility and limitation

The existing local-only 40K × 8 KiB workload completed under the same 256 MiB
zero-swap envelope with zero OOM counters, but its cgroup `memory.peak` reached
the 256 MiB ceiling. That result does not establish tiered feasibility.

The tiered path has a materially harder envelope. The existing 100K × 2 KiB
strict RustFS run at a 4 GiB limit observed approximately 134 MiB of active
cache, 256 MiB of `.tmp` staging, and a 1.408 GiB cgroup memory peak. The code
hard-limits an individual segment and remote frame at 128 MiB, while the
documented cache setting covers idle bytes only. Therefore this 40K × 8 KiB
command is a valid bounded qualification attempt, but it must be treated as
unqualified until the gate proves zero OOM, memory peak within 256 MiB, active
cache within one segment plus the idle budget, and staging within two segment
bounds. If the 256 MiB gate fails, the next diagnostic is a controlled repeat
at 512 MiB with the same binary, prefix, payload, batch, and cache settings to
separate cgroup pressure from a functional tiered-storage defect.
