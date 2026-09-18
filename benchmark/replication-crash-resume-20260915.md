# Replication crash-resume qualification — 2026-09-15

Campaign: `REPLICATION-CRASH-RESUME` (`tests/TerraBase.Recovery.Tests/ReplicationCrashResumeCampaign.cs`,
driven by `ReplicationCrashResumeTests`). Evidence: `.codex/evidence/replication-crash-resume-20260915/`
(authoritative `verification-receipt.md` + per-case JSONs + `kills/` SIGKILL evidence).

## What was measured

Source seeded with 6 batches x 32 records (192 rows, 24 tombstones). The replication
TARGET ran in a child process (`TerraBase.Recovery.Child`, mode `replicate`), was
SIGKILLed at each acknowledged batch boundary (after batches 1..5), and a fresh child
over the same root resumed to completion.

| case | exit | journal rows at kill | resume frontier | resumed batches | final rows | tombstones |
| ---- | ---- | -------------------- | --------------- | --------------- | ---------- | ---------- |
| kill-after-batch-1 | 137 SIGKILL | 32 | 32 | 5 | 192 | 24 |
| kill-after-batch-2 | 137 SIGKILL | 64 | 64 | 4 | 192 | 24 |
| kill-after-batch-3 | 137 SIGKILL | 96 | 96 | 3 | 192 | 24 |
| kill-after-batch-4 | 137 SIGKILL | 128 | 128 | 2 | 192 | 24 |
| kill-after-batch-5 | 137 SIGKILL | 160 | 160 | 1 | 192 | 24 |

Asserted per case: distinct dots == row count (exactly-once/dedup), contiguous dot
sequences 1..192 (no partial batch), live bodies deep-equal to source, tombstone keys
read deleted with retained evidence, applied frontier 192, journal identity stable
across the kill, resumed child exit 0.

Duplicate replay: batches 3-4 re-pushed after full replication, 32 Duplicate outcomes
each (64 total), zero applied/merged/deferred, durable high-water unchanged at 193
(no new WAL rows), final journal still 192 rows with intact bodies.

## Boundaries (honest split)

- Real MagicOnion: one leg pushed all 192 publications one-per-request through the
  real `MagicOnionInterNodeClient` over authenticated loopback TLS (client cert
  required, membership epoch 7, replica epoch 9) into a real in-process
  `TerraBaseHost` member: 192/192 Applied with durable acknowledgement
  (`magiconion-leg.json`).
- Cross-process crash/resume legs: records crossed the process boundary as production
  `ReplicationPushRequest` MessagePack frames over the named handshake pipe; the pipe
  substitutes the gRPC/mTLS transport only. Target-side `DurableReplicationRuntime.
  ApplyAsync`, `StorageEngine` WAL append, WAL replay, and runtime rehydration were
  the real production path, in a SIGKILLed child process.

## Verification

- Build: `0 Warning(s) / 0 Error(s)` (strict analyzers, warnings as errors).
- New selector `/*/*/ReplicationCrashResumeTests/*`: passed (1 test, ~18s, repeatable).
- Existing suites still green: `ProcessDurabilityTests` 3/3, `RecoveryChildProtocolBoundaryTests`
  4/4, `StorageEngineRestoreSessionLeaseP1Tests` 2/2.
