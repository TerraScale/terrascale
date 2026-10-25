# 0016: A healthy rewrite copies off the writer and holds it only for a short final catch-up

- Status: proposed (RT2d, 2026-10-25; option 1 agreed by COS under Q-REWRITE; awaiting human review before the code PRs).
- Scope: `TursoStorageEngine.Rewrite.cs`, `TursoDatabaseStore.Rewrite.cs`, `TursoChangeTap` (log replay),
  `TursoStorageTransaction.SuspendCaptureAsync`; callers in `Features/Management/HistoryErasures.cs`.
- Prompted by batch 8d production (2026-10-25, 17:25–19:30Z), where `terrabase.storage.writer_held{holder="a healthy rewrite"}`
  exceeded 1 s on every rewrite:
  - gru: up to 30 s;
  - sjc and fra: up to 10 s;
  - 2–4 rewrites per region after the 17:35 account deletions.

## Context

- Account history erasure ([history-erasure.md](../history-erasure.md)) rewrites the ts-auth platform database and SYSTEM on
  every holder, then rewrites again for residue and retirement.
- `TursoStorageEngine.RewriteAsync` holds two locks for the whole rewrite:
  - the store quiesced (`QuiesceAsync`: writer, operations and readers);
  - the engine-wide lifecycle lock (`EnterCreationAsync`).
- Under those locks it runs:
  - the row-by-row copy (`TursoCopy.Write`);
  - a logical digest of source and copy (both full scans);
  - a TRUNCATE checkpoint;
  - the target's initialization;
  - the rename.
- So every ts-auth write and every catalog anchor waits the whole copy; ts-auth answers Unavailable and anchors miss their
  10 s deadline. Creates and uncached opens of every other database wait too.

## Decision

1. **Copy without the writer.** Under neither the writer nor the lifecycle lock:
   - open a read transaction on the source;
   - record its commit offset and its last change id `C0`;
   - copy with `TursoCopy.Write`;
   - compare `RewriteDigest` of the copy with the snapshot, in the same read transaction.
2. **Catch up without the writer, in rounds.** Replay the source's change log after the last applied change id onto the
   scratch file, with the replay continuous-backup restore uses: `TursoChangeTap.ApplyAsync`, rowid-based, one transaction
   per commit record (`TursoCopy` keeps every rowid for this).
   - Repeat while a round applied more than a bound: at most 1,024 changes, or a round longer than 250 ms.
   - Give up, with the rewrite refused and retried later, after a bounded number of rounds: the database is writing faster
     than the copy catches up.
3. **Final catch-up under the writer, once.** Then:
   - quiesce the store;
   - take the lifecycle lock;
   - re-check that the store, its incarnation and lineage are the ones copied;
   - replay the last delta (bounded by step 2);
   - rename;
   - publish (`RewroteHealthily`, `Rewritten`).

   The writer is held for one bounded delta plus the rename: the target is under 1 s even on a saturated disk.
4. **Every change is captured while a rewrite runs.** `SuspendCaptureAsync` keeps capture on for a store with a rewrite in
   progress, so the log the catch-up replays holds every change. The housekeeping it suspends today (feed and shape
   retention, GC, shape frontier and lineage) writes rows the copy keeps; missing them would fail the digest or leave stale
   rows.
5. **The replay writes the new file's change log.** The catch-up applies the delta on a capturing connection, so changes
   committed during the copy are in the new file's log under the new incarnation. Peers and the backup tap receive them
   after the swap, as they receive any change of a rewritten file (the healthy-predecessor chain). Re-delivery is
   idempotent (lattice joins).

## The erasure guarantee is kept

- Erasure applies its cut under the writer *before* it asks for the rewrite, with `minimumOffset` at or after the cut. The
  snapshot of step 1 is taken after that commit, so no erased row is in it.
- The catch-up replays only changes after the snapshot. Those are writes made after the cut, which the erasure fence keeps
  from naming the erased subject.
- The old file's change log, which holds the erased history, is never copied: `TursoCopy` leaves change capture out, and
  the replay writes new log rows.
- The old file is deleted after the rename, as today.
- A rewrite refused in step 2 or 3 leaves the old file in place and writes no receipt; erasure retries, as today.

## Not changed

- Rewrite triggers, erasure receipts, lineage rules, the TRUNCATE checkpoint semantics and the rename durability handling
  (`RewriteUncertainException`, fail closed) stay as they are.
- No storage format change.

## Plan (PRs of at most ~400 lines each)

1. Copy and digest off the writer, catch-up rounds with the final delta under the writer, and the lifecycle lock only for
   the swap.
   - Fast test: a rewrite of a database of about 50 MB while writes continue; `writer_held{holder="a healthy rewrite"}`
     stays under 1 s, measured on a real clock.
   - Fast test: every write committed during the copy is present in the new file.
2. Capture kept on during a rewrite (decision 4).
   - Fast test: housekeeping that runs during a rewrite is in the new file.
3. Erasure regression: the existing `HistoryErasure*` suites, plus a Fast test that a rewrite racing an erasure cut never
   carries an erased row and still deletes the old file.

## Open questions for review

- Bounds of step 2: 1,024 changes or 250 ms, and the number of rounds before refusing.
- Whether SYSTEM rewrites may skip shape tables entirely (SYSTEM has none today).
