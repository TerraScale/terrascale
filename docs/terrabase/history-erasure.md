# Shared-history erasure: ts-auth database and SYSTEM (TS-873)

After ts-auth logically deletes a subject, TerraBase removes the deleted rows' history from every holder's copy of ts-auth's
database (`ts-auth`, fixed ids in `PlatformDatabases`) and of SYSTEM with the healthy rewrite (`IHealthyRewrite`).
`deletion-retention.md` describes the surrounding deletion evidence; `project-closure.md` the project handoff.

## Peer operation

`PUT /admin/history-erasures/{erasure}`, internal listener only (peer credential). SDK:
`TerraBaseManagementClient.RequestHistoryErasureAsync(erasureId, requestedAt, settlementToken)`. `erasure` is a random ULID
ts-auth mints with the deletion job (`DeletionJob.ErasureId`). Before asking, ts-auth writes its request document
`history_erasure_marks/<erasureId>` = `{"kind":"request","erasureId":…,"fenceId":…}` in its own database; holders read the
fence id from their copy of it. The caller names no database. The machine that creates the record is its requester: only it settles, finalizes and verifies
backups, so no second machine writes the record (another region's ts-auth gets the status, without a token). Body `{"version":1,"requestedAt":…,"settlementToken":null|ULID}`.

| Status | Meaning |
| --- | --- |
| 202 `fencing` | Some holder has not committed its fence marker. `pendingHolders` counts them. |
| 202 `settling` | Every holder marked; this machine has not applied every marker yet. |
| 202 `reverify` + `settlementToken` | This machine covers every marker: re-run auth's scoped cleanup against it, then present the token. |
| 202 `rewriting` | The final cut exists; some holder has not rewritten both files from it. |
| 202 `residue` | Every holder rewrote; some still has a tombstone or feed event at or below the cut in its auth file. |
| 202 `backup` | A platform backup object, hidden version or upload older than the rewrites remains, or a bucket snapshot from before the first clean inventory. |
| 202 `retire` | Certified: the subject's history is gone from every certified holder and backup. ts-auth may retire the fence; then each certified holder rewrites once more. |
| 202 `epoch_stalled` | A holder restarted uncleanly (new replica epoch) since it marked: see "Replica epoch change". Never completes on its own. |
| 200 `complete` | All of the above done, retirement rewrite included. |
| 409 `erasure_conflict` / `settlement_stale` | Another request time; a token that is not this machine's current one (single use). |

`activeOverdue` / `backupOverdue` use deadlines from auth's original `requestedAt`: active +30 days, backup + the longest
configured retention + 30 days. A retry never moves them.

## Barriers (committed writes, never clock reads)

1. **Fence marker**, on each holder P: one durable auth write transaction reads the request document, checks
   `deletion_jobs/<fence>` is live, reads P's applied vector and writes P's marker. The marker's committed stamp S_P is read
   back before the SYSTEM receipt is written. Stamps mint under the single writer, so every later write of P's origin ran
   with the fence present (ts-auth asserts its absence in every fenced write) and every earlier one is below S_P. The
   receipt's own SYSTEM version is P's SYSTEM marker.
2. **Settlement**, on the requester N, from a snapshot of each file (the final cut rechecks under the writers): each holder's marker is present with exactly S_P and
   `applied[P] >= S_P`; SYSTEM `applied[P] >= receipt version`; any origin a holder reported but nobody marks (a retired
   replica) is applied at least as far as any holder reported. N's own origin is local. Only then N issues its token.
3. **Final cut**, on N after auth re-verified: one durable auth write transaction re-checks coverage, reads N's applied
   vector and writes N's final marker. The cut is that vector plus the marker's committed stamp. SYSTEM's is taken the
   same way in one SYSTEM transaction with `history_erasure_finals/<erasureId>`.
4. **Rewrite**, on each holder H: under each file's writer, every cut entry is applied and the finalizer's marker is
   present. Then H reads its own commit offset and runs `RewriteAsync(file, offset)`. Receipt `rewritten` binds the final id,
   both rewrites' source/target incarnations and offsets.
5. **Residue**: once the auth file holds no deleted state and no feed event at or below the cut, H rewrites it once more
   (receipt `residue`). Normal tombstone collection and 7-day feed retention get there. No floor is forced.

Each rewrite must replace exactly the file its coverage was read from (`SourceIncarnation` equal to the incarnation read with
the coverage), else no receipt. Each file's lineage is recorded as its rewrite succeeds, so a refused second file does not
make the first look restored. A refused file waits before its next attempt, doubling from 10 s to an hour; unhealthy files
are refused before the rewrite ends the backup tap. A cut beyond 1024 origins is refused, never truncated.

Holders are the expected holders plus current catalog members (`EngineManagementService.CatalogHoldersAsync`, the
closure rule). A refused rewrite writes no receipt and retries; an uncertain one leaves the node failed closed.

A request that pinned an incarnation a durable healthy rewrite replaced is still admitted: `RebuildLedger.RefusedAsync`
follows the store's in-memory chain of healthy rewrites to the file in place. A copy fault, a rebuild, any other
replacement and failing closed still refuse.

## Offline holders and the day-20 alert

A listed holder that is offline keeps the erasure (and a database purge) pending, and keeps its tombstone collection hold:
nothing evicts it, because its acknowledged but unshipped writes would be dropped below the floor on return (refill does
not restamp them). Removing a region is an explicit user decision; nothing does it automatically.

Gauges (exported names): `terrabase_history_erasure_oldest_age_seconds` and `terrabase_purge_oldest_age_seconds` are the
true age of the oldest pending request (an erasure that fails before it can be judged counts as pending at its age);
`terrabase_history_erasure_pending`, `terrabase_history_erasure_active_overdue`, `terrabase_history_erasure_backup_overdue`
and `terrabase_history_erasure_epoch_stalled`. The ts-orchestrator rules (#145) alert on the age at 20 days and on
`terrabase_history_erasure_epoch_stalled > 0`.

## Replica epoch change (TS-875)

An unclean restart gives the machine a new replica epoch, and peers delete the old epoch's `_tb_peers` row, applied
progress included, on its first acknowledgement (`TursoStorageTransaction.Tail.cs`). Nothing then proves that every holder
holds the old epoch's acknowledged writes, and SYSTEM keeps no per-origin history to show it. The holder fences again from
its new epoch and keeps its older epochs in its receipt (`PreviousReplicas`); an origin a holder reported that is an older
epoch of a marked holder counts the same. The erasure is `epoch_stalled`: no token, never complete, never inferred from the
newer epoch, the own old epoch never treated as local, no GC override.

Runbook: the epoch_stalled alert fires within 15 minutes. Escalate to engineering (Ku TS-875, "Safe automatic erasure
re-cut after replica epoch change"). There is no operator override. The gauges are read on the metrics listener (no credential):
`curl -s http://<node>:<metrics-port>/metrics | grep terrabase_history_erasure`.

## What is kept, and for how long

**SYSTEM, permanent (live documents and their tombstones):** `history_erasures/<erasureId>`,
`history_erasure_receipts/<erasureId>/<machine>`, `history_erasure_finals/<erasureId>/<machine>`,
`history_erasure_lineage/<machine>`. Ids are the random erasure id and machine names. Fields are random ULIDs (final id),
machine names, replica ids, HLC stamps, commit offsets, file incarnations and times. Before certification the record also
holds a token and two digests (`SettledMarks`, `FinalMarks`: SHA-256 over machine name, replica id and marker stamp, not over
any document content); certification clears them.
No subject id, fence id, document content, field value, name, email or user-chosen key.

**Auth database, until fence retirement:** the deletion job (instance id, scope, subject id, organization id: generated
ids), the request document (erasure id, fence id: a natural-key hash) and the holders' markers (erasure id, machine name,
nonce, final id). ts-auth retires them in one transaction (job, markers, request document last) once TerraBase reports
`retire` and `fenceExpiresAt` (request + 15 days) passed. Then each certified holder waits until its auth file holds no
tombstone or feed event at or below the request document's deletion and rewrites once more; only then is the erasure
`complete`. Holders joining after the certificate copied the erased files and are not rewritten again: the retirement's
deleted pages stay in their files until their next rewrite (tombstones leave by collection).

**Offline-region exception (Q-OFFLINE-AUTH):** while a listed holder is offline, the pending job's opaque generated subject
and organization ULIDs stay (no emails, names or content), and the normal job clears them once the holder returns and the
erasure completes. The day-20 alert applies. This exception does not cover an epoch stall (TS-875, above) or any permanent
auth hash.

**Fence lifetime:** sessions and OIDC refresh families end 14 days after issue and rotation never extends them; access JWTs
and GitHub grants end within 24 h, invitations within 7 days; stored credentials die with the root or cleanup. Jobs written
before these fields keep their logical cleanup but never get a history erasure (bootstrap or wipe; no migration).

## Backup closure

By the requester, in two steps. (1) An inventory finds every snapshot and log generation of the auth file started after the
latest residue rewrite and every one of SYSTEM after the latest SYSTEM rewrite (a generation never spans a rewrite: the
rewrite ends the backup tap), and no noncurrent version, delete marker or earlier multipart upload under either prefix
(backup keys are written once, so a versioned bucket's hidden version is a removed object). Its time is kept as
`BackupCleanAt`, bound to the final cut. (2) Bucket snapshots taken up to that time can still hold older objects: only when
none remains is the record certified (`BackupVerifiedAt`, `BackupFinalId`, `CertifiedHolders`). The digests and token are
then cleared. Expiry is the existing plan retention and the 14-day bucket snapshot retention; this only verifies it. A bucket
whose noncurrent versions never expire keeps the erasure at `backup` forever (fail closed). It needs the platform bucket
(`S3ObjectStore`); without backup configured the erasure stays `backup`.

## Not handled

- A simultaneous rollback of ts-auth's database and SYSTEM before backup closure is not detected. A single file replaced
  by a restore, rebuild or copy is detected (`history_erasure_lineage`) and fenced again. A SYSTEM-wide rollback is recovered
  by auth presenting its original request time until it retires the fence.
- SYSTEM tombstones are permanent (only opaque ids, see above).
- A replica epoch change stalls the erasure (TS-875).
