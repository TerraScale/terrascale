# Native backup implementation and upstream study

Reviewed upstream: Litestream commit
`4ed7a308f6271ebfd2b0a6e4b70b03011a37e4a3` (2026-09-05 checkout),
<https://github.com/benbjohnson/litestream/tree/4ed7a308f6271ebfd2b0a6e4b70b03011a37e4a3>.
This is an implementation record for ADR 0046, not qualification evidence.
No upstream code was copied; upstream remains Apache-2.0 licensed.

## Cleanup and replacement boundary

`crates/terrabase-server/src/backup.rs` is reachable from public backup,
branch/restore and member startup paths. Its process supervision and packaged
Litestream executable therefore cannot be deleted independently. The replacement
must serve those callers and pass native recovery and comparative performance
gates first. Existing `src/backup` is active native logical backup/recovery code,
not obsolete storage: it exports engine publication state and ordered journal
changes into encrypted, manifest-bound recovery points. Its tests and evidence
are retained. The unused `RestoreResult` compatibility alias had no callers across the workspace and was removed with its sole documentation reference. No active backup path was deleted.

## Reviewed source and recovery lessons

| Source at pinned revision | Reviewed behavior | TerraBase consequence |
| --- | --- | --- |
| `db.go:verifyAndSyncWithExecutor`, `verifyWithExecutor`, `sync` | Verify WAL salt/offset continuity before capture; logical WAL end differs from file length; checkpoint lock protects capture; LTX files represent transaction boundaries | Use the engine's durable publication journal for native logical capture. Never infer an incremental boundary from WAL file length. Physical capture remains behind `Engine::with_quiescent_wal`. |
| `db.go:checkpointWithExecutor` | Capture before checkpoint, then seal writes under a barrier; compare WAL restart state | Backup retention is independent of follower acknowledgement. Native worker pins `consumer_frontiers['native-backup']`; only every selected provider's complete readback advances it. |
| `replica.go:syncOnce` | Serialize replica sync; recompute remote position on error; bounded batches | One shared fleet tick bounds in-flight capture/upload and retries every selected provider independently. Upload index is last; retry never treats a PUT acknowledgement as verified coverage. |
| `replica.go:calcRestorePlan`, `applyNewLTXFiles` | Select complete paths; bridge compacted gaps, refuse unsupported gaps; verify restore before follow progress | Restore authenticates every manifest/member and validates predecessor boundaries before publication. One provider supplies its own listing and complete chain. |
| `db.go:EnforceSnapshotRetention` | Retain a root even when all snapshots are old; conservative floor preserves in-flight restore plans | No age-only deletion of segments. Current worker stops when its explicit point/staging cap is reached; automated chain-preserving retention remains required before replacing the adapter. |

Reviewed tests include `TestReplica_RestoreRetriesInitialLTXOpenError`,
`TestReplica_Restore_InvalidFileSize`, `TestReplica_Restore_RemovesTempFileOnFailure`,
`TestReplica_Restore_Follow_CrashRecovery`, `TestDB_SyncAfterVacuum`, and
`TestStore_EnforceSnapshotRetention_RetainsInFlightRestorePlanFiles`. Source issue
references reviewed: #724 (RESTART checkpoint write blocking), #927 (unnecessary
snapshots on restart), #997 (stale WAL frames affecting checkpoint thresholds),
and #896 (checkpoint bookkeeping producing idle work). These references are
source observations, not a claim that the upstream test suite ran here.

## Implemented boundaries

The Rust worker uses shared bounded scheduling, an explicit per-database staging
budget and maximum recovery-point count, database key leases, and separate OCI
and Tigris clients. It captures logical state/journal records through the existing
engine backup core. Full and incremental artifacts are encrypted. Recovery leases
reconstruct authenticated artifact contexts after process restart, and readers
can resolve retained key versions. No writer acknowledgement waits for object
storage. Failures preserve staged data and journal retention protection.

Encrypted incremental discovery now authenticates encrypted predecessor
manifests; the previous implementation searched the plaintext namespace only.
Idempotent publication now verifies existing member bytes as well as the manifest
and propagates directory fsync failures. Staging limits are checked before record
bytes are written. Remote uploads verify each object by readback and publish a
bounded transfer index last. Restores obtain an independent provider listing,
verify ciphertext transport digests, then authenticate the native archive before
publishing a new engine location.

## Qualification still required

This is not yet ADR 0046 completion. Production catalog/runtime composition,
automated retention with restore leases, durable policy transitions, timestamp
PITR selection, journal-frontier-complete physical bootstrap, and physical WAL
capture/checkpoint/reuse fault qualification remain required. The logical archive
is not advertised as a complete physical SQLite image. The fleet's pending-age
gauge measures observed unprotected work, not the exact last remotely recoverable
commit time; a ten-second timer alone does not establish ten-second RPO.

Actual OCI and Tigris bucket placement, credentials, outages/throttling, key
revocation/deletion, fourteen daily restore drills, and comparative measurements
against pinned upstream and the existing adapter require intended provider and
host access. No local object-store fixture establishes provider qualification.
Comparisons must retain ADR 0046's database counts, hardware, encryption,
durability and workload controls and report lag/RSS/CPU/disk/object-request
tradeoffs in `bench/` before the external adapter is removed.
