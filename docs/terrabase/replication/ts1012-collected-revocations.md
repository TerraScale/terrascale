# TS-1012: catalog admission after collected revocations

A restarted catalog, or a fresh catalog seeded from a stale holder, can contain a live PUT that its author has already
deleted and collected. State-only anti-entropy cannot prove that deletion; the old snapshot importer refused the whole
snapshot rather than discarding the live row. The gate consequently never acquired the author's newer external cut.

## Evidence and limits

- `TursoChangeGroupSource.TrimAsync` calls `GetHorizonAsync` and then `CollectAsync`. `TursoStorageTransaction.Gc.Bounds`
  takes the minimum of local received applied stamps and peer-reported stable stamps, subtracts 60 seconds and retains
  the previously committed collected floor. CDC acknowledgments do not enter that calculation. A listed missing origin
  holds progress at zero. `CollectAsync` deletes only non-live states whose entire state is below the horizon.
- History erasure checks the final auth and SYSTEM coverage before rewriting (`HistoryErasures.RewriteAsync`). Residue
  and retirement wait for deleted history to disappear through ordinary collection. They do not force a floor.
  `TursoDatabaseStore.WriteRewriteAsync` compares the copied logical digest before replacement. `TursoCopy` resets CDC
  acknowledgments and changes file incarnation, preserving received applied coverage and state.
- `GetAckedAsync` explicitly returns a zero cursor requiring refill for an old incarnation; it does not carry its old
  stable expectation. `PeerStreams` sends a refill before ordinary batches. A successful refill records coverage;
  a refused refill leaves coverage unchanged. `RequireAppliedAsync` can refuse a missing prefix, but rewrite alone is
  not evidence that this expectation becomes permanently unreachable.
- `AntiEntropy.PullAsync` joins repairs without advancing applied. `CatalogGate.VerifyAsync` verifies peers through
  those pulls before `RevocationsHeldAsync` imports snapshots. This lets a stale holder seed a fresh catalog with a
  live survivor. `ReconcileRefillAsync` then refuses it below the source floor.

The original 17:29 stream stall and the production collection horizon are not established by these paths alone. In
particular, an acknowledgment reset cannot explain collection beyond a stalled listed peer's stable stamp. No claim is
made that removing unrelated closed files caused it. The Fast reproduction holds catalog stream application and passes
an explicit cut to the real collector; it reproduces the post-collection state, not its unknown production cause.

## Recovery proof

Only SYSTEM whole-document PUT survivors qualify for additional reconciliation: a live clear, every field base at that
same version, and no counter or set entries. All provisionally forgotten live PUTs must belong to the snapshot author's
current replica identity, epoch included. The terminal frame identifies that author. Its own committed local cut, exported
from the same native read snapshot as the states and collected floor, must cover those PUTs. A third party's received
applied vector cannot authorize this forget.

The source must pass the existing trusted-catalog check on `ReadSnapshot`; a restored catalog cannot provide this proof.
The importer forgets provisionally under its durable writer transaction, then requires a terminal frame, clean EOF,
author match and sufficient author cut before committing any state, floor or coverage. Failure rolls the transaction back.
An absent PUT the source never authored, an old epoch, a mixed-origin state or an insufficient cut still fails closed.

This recovers both an existing stale catalog and a fresh one after stale verification pulls. The inherited collected
floor rejects subsequent repairs carrying the old revoked PUT. Import-before-pull alone cannot recover already seeded
or restarted catalogs, so changing admission order is insufficient. Tenant snapshot and ordinary anti-entropy refill
contracts keep their existing refusal of live absences. There is no storage schema/format change or operator bypass.

The peer snapshot protocol adds terminal `author_replica` evidence. A source without that evidence cannot authorize
forgetting a live PUT; received-prefix evidence continues to require a complete snapshot and clean EOF.

Validation on `d834a463a` plus this change (Fast, default parallelism):
- `CatalogGateCollectedRevocationTests`: original production code 0 passed / 2 failed (32.522 s, writer import false); fixed 2 passed / 0 failed (7.190 s).
- `CatalogSnapshotProofTests`: 6 passed; `ReplicaSnapshotTests`: 6 passed; the writer, restart, rewrite-restart and healthy-rewrite-refill regressions: 5 passed.
- Requested Fast build: 0 warnings / 0 errors. These are focused integration runs; the complete Fast and Slow suites were not run.
