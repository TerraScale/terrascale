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

## Production survivors

Forensics on the kept catalogs (2026-10-25, read-only, ids hashed): 140 rows were live in fra (49) or old gru (91) and
absent from sjc. sjc's collected floor was about 17:27:56Z; fra and old gru had applied sjc up to 17:29:09Z, and sjc had
applied fra and old gru past 21:00Z, after every survivor (13:24Z to 20:09Z).

- 136 were `databases/<db>/usage*` metering documents, most of a deleted database. Their row origin was 0 (the projection
  a collection drop writes for states it keeps) or old gru or fra. None was written by sjc.
- 4 were `api_keys` and `api_key_order` documents written by sjc at 17:17:07Z.

A rule limited to the source's own whole-document PUTs would have recovered only the 4.

## Recovery proof

A snapshot source collected a live local state's absence only if it applied every operation of that state and then
deleted the document and collected its tombstone. Every operation still visible in a state names its author: the clear,
each field base, each node's counter entry, each set add and remove. None is stamped later than the state's `MaxStamp`
(a counter entry's `Changed` is its last increment). The importer therefore forgets a live SYSTEM state below the
source's floor only when, for every author of that state, the source's terminal applied cut for that author is at least
the state's `MaxStamp`. A state naming replica 0 or the reserved maximum fails closed. A counter's epoch names the base or
clear it counts from, so it is not an author.

The cuts are those the catalog gate already trusts: a received cut covers every operation of its origin up to its stamp
(streams apply in order; a refill records coverage only after a complete snapshot). The source's own entry is its
committed local cut, exported from the same native read snapshot as the states and floor; a received row for the
source's own replica is dropped from the export.

The source must pass the existing trusted-catalog check on `ReadSnapshot`; a restored catalog cannot provide this proof.
The importer forgets provisionally under its durable writer transaction. It commits states, floor and coverage only
after a terminal frame, clean EOF and cuts covering every forgotten state; any failure rolls the transaction back. A
state with an author the source has not applied far enough still fails closed.

This recovers both an existing stale catalog and a fresh one after stale verification pulls. The inherited collected
floor rejects subsequent repairs carrying the old revoked state. Import-before-pull alone cannot recover already seeded
or restarted catalogs, so changing admission order is insufficient. Tenant snapshot and ordinary anti-entropy refill
contracts keep their existing refusal of live absences. There is no storage schema or format change, no protocol
change and no operator bypass.

## Validation

- `CatalogGateCollectedRevocationTests`, four cases: restarted or fresh receiver, revoked row written by the snapshot
  source or by the stale region. Production code before TS-1012: the source-written cases 0 passed / 2 failed. A rule
  accepting only the source's own whole PUTs: the stale-region cases 0 passed / 2 failed. This rule: 4 passed.
- `CatalogSnapshotProofTests`: the untampered snapshot forgets the survivor; a missing author cut, an insufficient cut
  and failed trailers keep it.
