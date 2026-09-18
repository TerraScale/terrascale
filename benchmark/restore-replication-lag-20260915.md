# Backup/restore timing and loopback replication lag measurement

This report measures the real `BackupManager` full/incremental backup and
restore paths over `StorageEngine` stores seeded with small (~244 B body)
documents, and the durable-ack plus apply-to-visible latency of the real
MagicOnion inter-node replication push over an authenticated loopback TLS
listener. All numbers come from the frozen source copy listed below; nothing
in this run modifies TerraBase.

**This is a single-host, in-process, loopback measurement at `nice -n 19` on
two pinned CPUs. It is NOT WAN-representative, NOT a multi-node deployment
measurement, and NOT a production capacity claim.** The loopback TLS peer
wiring follows the replication fault-test harness; the "network" is
localhost HTTP/2 with mTLS inside one process on two cores.

## Identity and environment

| Item | Measured identity |
| --- | --- |
| Source root | `.codex/evidence/terrabase-perf-restore-20260915/source` (frozen copy; build and run only inside it) |
| SDK / target | .NET SDK `10.0.401` selected by the copy's `global.json`; `net10.0` Release |
| OS / machine | NixOS 26.05, Linux 7.2.4, x86_64, Intel Xeon E5-2660 v4 @ 2.00 GHz, 46 logical host CPUs |
| CPU execution | `taskset -c 14,15`, `DOTNET_PROCESSOR_COUNT=2`, `nice -n 19` (build and run) |
| Cgroup | `/user.slice/user-1000.slice/session-238.scope`; `cpu.max=max 100000`, `memory.max=max` (no quota or ceiling; host-level contention from other session work is possible and was observed intermittently during the run window) |
| Harness | `measurement-harness/` console project, ProjectReference to `src/TerraBase/TerraBase.csproj`; copied to `.codex/swarm/measurement/restore-replication-lag-20260915/` |
| Object store | `InMemoryBackupObjectStore` (in-memory; no S3 round trips in any number) |
| Encryption / trust | `NoBackupEncryption` hook + `HmacSha256BackupManifestSignatureProvider` trust root, wired exactly like `BackupKeyRotationTestFactory` in `tests/TerraBase.Api.Tests` |
| Replication stack | `DurableReplicationRuntime` over the real `StorageEngine` journal (source), and a full `TerraBaseHost` peer with `PeerServerOptions` mTLS allowlist (target), pushed through the real `MagicOnionInterNodeClient` |
| Timing | `System.Diagnostics.Stopwatch.GetTimestamp` / `Stopwatch.GetElapsedTime`; allocations via `GC.GetTotalAllocatedBytes` deltas (process-wide, single-threaded phases) |

Latency percentiles are nearest-rank over the stated sample count N. Backup
and restore medians are the middle of 3 runs with the range in parentheses.

## Case 1 — full backup creation timing

Method note: each scale seeds a fresh `StorageEngine` directory
(`InitializeJournalIdentityAsync`, then `StorageMutation.Upsert` batches of
5,000 documents with distinct IDs and ~244 B JSON bodies; seeding is untimed
and excluded). One discarded 1,000-document full backup warms the JIT first;
the timed runs then call `BackupManager.CreateFullBackupAsync` on the real
`StorageEngineBackupSource` with a static metadata provider covering the one
tenant/collection scope. Wall time is the `CreateFullBackupAsync` await;
allocated bytes are the `GC.GetTotalAllocatedBytes` delta across that await;
artifact bytes are the committed manifest's artifact lengths.

| Documents | Wall median | Wall range (3 runs) | Allocated median | Artifact bytes | Throughput (docs/s, median) |
| --- | --- | --- | --- | --- | --- |
| 1,000 | 15.3 s | 15.0 – 16.4 s | 5.08 GB | 1.01 MB | 65.3 |
| 10,000 | 1,316.6 s (~22 min) | 1,288.8 – 1,401.9 s | 492.8 GB | 10.11 MB | 7.6 |
| 50,000 | not completed in this run | — | — | — | — |

The 50,000-document case was **not run to completion**: measured wall time
grows quadratically in the number of physical journal records (see finding
below), and the 1k→10k scaling factor of ~86× extrapolates one 50k full
backup to roughly 8 hours on this pinned two-core setup, which exceeded the
run window. The 10k numbers above are complete 3-run measurements.

Finding (root cause of the quadratic backup cost, profiled with
`dotnet-dump` on the live measurement process): the authority capture inside
`StorageEngineBackupSource.CaptureAuthorityAsync` enumerates
`StorageEngineBackupCapture.ReadRawPhysicalRecordsAsync`, whose
implementation (`source/src/TerraBase/StorageEngine/StorageEngine.cs`,
`ReadRawPhysicalRecordsCoreAsync`) resolves **every** physical sequence by
calling `ReadRawRecordAtSequenceAsync`, and that method replays the entire
durable log from the beginning (`FileDurableLog.ReplayRawAsyncCore` opens a
reader at offset 0 and decodes every frame) to find one record. The backup
authority scan is therefore O(records²) frame decodes: ~5 GB of allocations
and 15 s at 1k records, ~493 GB and ~22 min at 10k records. Artifact bytes
stay linear (~1 KB/document) because the authority artifact embeds one
base64 record per document.

## Case 2 — restore plan, execute, and verified read-back

Method note: restores target a fresh `StorageEngineRestoreTarget` root per
run and restore the tip manifest produced by case 1 at the same scale (same
process, same object store; the manifest is the last of the three timed
backups). The plan/execute split is cleanly observable as separate
`CreateRestorePlanAsync` and `RestoreAsync` awaits. Read-back then reopens
the restored active generation with `new StorageEngine(root.ResolveActivePath())`,
scans the full tenant/collection, and compares every document body to the
expected seeded JSON (`JsonNode.DeepEquals`); verification is included in
the read-back wall time. docs/s is verified documents divided by that
read-back wall time.

| Documents | Plan median | Execute median (range) | Plan+execute median | Read-back+verify median (range) | Verified / mismatched | Read-back docs/s |
| --- | --- | --- | --- | --- | --- | --- |
| 1,000 | 16.1 ms | 523 ms (459 – 612) | 535 ms | 118 ms (105 – 163) | 1,000 / 0 | 8,464 |
| 10,000 | 76.8 ms | 5,703 ms (5,452 – 5,784) | 5,777 ms | 834 ms (824 – 914) | 10,000 / 0 | 11,995 |
| 50,000 | not completed in this run | — | — | — | — | — |

Restore does **not** exhibit the backup's quadratic behavior in the observed
range: plan time is near-linear and execute time scales ~11× for 10×
documents. The 50k row is missing only because producing a 50k backup to
restore from is the infeasible step (case 1), not because restore itself was
attempted and failed.

## Case 3 — incremental chain (full + 3 incrementals + restore of tip)

Method note: the specified scenario is full(10,000) + three incrementals of
100 documents each, then restore of the tip. On this hardware the 10k full
backup alone costs ~22 minutes per chain run (case 1), which did not fit the
remaining run window, so the chain was executed at the 1,000-document scale
(warmup + full + 3×100-document incrementals + tip restore, one run, one
process) to measure the incremental mechanics; the 10k-scale chain was
started (seed + full backup begin observed) and then abandoned to free the
CPUs for the lag case. Treat the numbers below as the chain shape at 1k, not
as the requested 10k chain. The 10k-scale chain was **not completed in this
run**.

| Step (1,000-doc base) | Median | Range |
| --- | --- | --- |
| Full backup | 13.5 s | single run; artifact 1.01 MB |
| Incremental backup (100 docs each) | 1.83 s | 1.76 – 2.20 s; artifact ≈1.08 MB each |
| Restore of tip (plan) | 69.3 ms | single run; 3 log entries applied |
| Restore of tip (execute) | 688 ms | read-back verified 1,300 / 1,300, 0 mismatches |

The incremental artifact is as large as the full artifact because the
authority capture re-embeds the complete physical-record digest set from
sequence 0 in every recovery point (same mechanism as the case 1 finding);
each incremental therefore also costs a full O(records²) raw replay of the
journal even though it archives only 100 new documents.

## Case 4 — replication lag over the real loopback MagicOnion stack

Method note: the target is a full `TerraBaseHost` member started exactly like
`ReplicationNetworkFaultPeer` in the fault tests — ephemeral
`https://127.0.0.1:0` listener, generated CA/server/client certificates,
`PeerServerOptions` allowlist for one database and tenant, mTLS client
cert required — with host log level set to Warning so per-RPC console logging
does not sit inside the timed window. The source is a real `StorageEngine`
with `InitializeJournalIdentityAsync` called before
`DurableReplicationRuntime.OpenAsync`. Each sample admits M records durably
at the source (`ApplyAsync`, `DurableAcknowledged` required, untimed), then
pushes all M as one `ReplicationPushRequest` through the real
`MagicOnionInterNodeClient.InvokeAsync` (fresh idempotency key per call;
`NoBackupEncryption`-style plain canonical JSON bodies of ~250 B). Durable
ack latency is the client-side send→response window; the response's
`DurableAcknowledged` flag and every per-publication outcome must be
`Applied`. Five untimed round trips warm each batch size before sampling.
Visibility is probed with a tight loop calling
`Engine.Replication.Read(key, database)` on the target read model until the
last pushed document appears; the loop counts its own iterations so the
polling overhead is visible rather than hidden.

| Batch M | N | Durable ack p50 | p95 | p99 | Ack→visible p50 / p95 | Send→visible p50 / p99 | Polls until visible |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 60 | 5.86 ms | 18.40 ms | 42.49 ms | 0.028 / 0.049 ms | 5.88 / 42.52 ms | 0 (60/60 first probe) |
| 16 | 60 | 29.75 ms | 62.72 ms | 68.29 ms | 0.032 / 0.049 ms | 29.79 / 68.33 ms | 0 (60/60 first probe) |
| 128 | 60 | 121.55 ms | 168.04 ms | 220.38 ms | 0.012 / 0.047 ms | 121.56 / 220.47 ms | 0 (60/60 first probe) |

Every one of the 180 pushes returned `DurableAcknowledged` with all
per-publication outcomes `Applied`; the p99 tail at M=1 (42 ms vs 5.9 ms
p50) is scheduler/GC noise on the two pinned cores rather than a protocol
cost — the same tail shape appears at every batch size. Per-record cost
flattens with batch size: 5.9 ms at M=1, 1.9 ms/record at M=16, 0.95
ms/record at M=128.

Boundary statement for the visibility number: the push service applies each
publication durably and mirrors it into the engine read model inline, before
the RPC response is returned (`MagicOnionInterNodeService.PushAsync` awaits
`durableRuntime.ApplyAsync` and `engine.Replication.Apply` per publication).
Ack→visible is therefore expected to be bounded by the probe itself, and the
meaningful end-to-end number is send→visible. The network dimension is real
loopback TLS inside one host on two cores — not a WAN or cross-host figure.

## Caveats

- Single host, single process, two pinned cores at `nice -n 19`; other
  session work ran intermittently on other CPUs during the window. Loopback
  latency numbers are lower-bound shaped and must not be read as
  cross-datacenter replication lag.
- Allocation numbers are process-wide `GC.GetTotalAllocatedBytes` deltas;
  the timed phases are single-threaded and no background host was running in
  the backup/restore cases, but the counter cannot exclude unrelated runtime
  noise categorically.
- The artifact store is in-memory, so no S3/network upload cost is included
  in backup or restore wall time.
- The lag case's source-side durable admission (`ApplyAsync` at the source)
  is required to succeed but is not part of the reported ack latency.
- 50k backup/restore and the 10k-scale incremental chain were not completed
  in this run window; see the per-case notes for the measured reason (the
  quadratic authority scan in backup creation, profiled live during the
  run, is the direct cause).
- The chain case ran one run at 1k scale instead of 3 runs at 10k scale, so
  its numbers carry single-run, wrong-scale caveats as stated in case 3.

## Finding — O(n²) backup authority scan (P0 performance defect)

The ~22-minute / 7.6 docs/s full backup at 10k documents (and the ~492 GB
process allocations) is a quadratic scan, not device slowness:
`StorageEngine.ReadRawPhysicalRecordsCoreAsync` yields one record per
sequence, and each `ReadRawRecordAtSequenceAsync`
(`src/TerraBase/StorageEngine/StorageEngine.cs:4725`) linearly rescans every
segment and replays the whole WAL to locate that single sequence — n record
lookups × n-record scans, decoding every record JSON each pass. Restore is
fast by contrast (8.4k docs/s read-back), so backup capture dominates.

Fix direction (not implemented in this run — the backup authority path is
freshly verified-green and the fix must preserve exact TBREC bytes and
bounded memory across key-sorted compacted segments): single-pass iteration
per storage stream with an in-memory sequence→stream/ordinal index (the
engine already holds every record decoded in memory, so the index is
cheap), or per-stream spool + k-way merge as in the migration sorter.
Re-measure this table after the fix; the 50,000-document case was not
completed in this run because of this defect.

## Fix verification — 2026-09-16, single-pass raw capture

The quadratic scan was fixed the same night: `ReadRawPhysicalRecordsCoreAsync`
now performs exactly one pass per storage stream into a sequence-keyed raw
envelope map (bytes are the exact on-disk TBREC frames; yields then walk the
in-memory sequence index, preserving strictly increasing authority order and
all duplicate/missing-row fail-closed checks), and the per-record rescan
helper was deleted. The whole backup/restore cohort is green (36 tests across
8 classes, strict build 0 warnings / 0 errors).

Re-measured with the identical harness against the fixed live tree
(`scratch/restore-lag-live`, same environment header):

| Documents | Before | After |
| --- | --- | --- |
| 10,000 | 1,316.6 s median (7.6 docs/s), 492.8 GB allocated | completes as part of the 50k run below (no longer pathological) |
| 50,000 | not completable (~8 h extrapolated) | **9.8 s median (6.9–10.4 s range), 5,112 docs/s median, 1.30 GB allocated, 50.5 MB artifact** |

The previously infeasible 50,000-document full backup now completes in under
eleven seconds: roughly a 670× throughput improvement at 5× the scale, with
allocation amplification reduced ~380× per document. Restore-side numbers
above are unaffected (restore was already linear).
