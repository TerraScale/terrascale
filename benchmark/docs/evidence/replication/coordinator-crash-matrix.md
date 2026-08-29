# Coordinator crash-point matrix lane receipt (gap-sweep row 9)

No production claim: lane-scoped facts from the deterministic in-process
lane only.

- Producing test file: `crates/terrabase-cluster/tests/coordinator_crash_matrix.rs`
  (commit `d1477b2`, branch `develop`)
- Run command: `cargo test -p terrabase-cluster --test coordinator_crash_matrix`
- Result at `d1477b2`: **9 passed; 0 failed** (0.53s). Supporting targets,
  same tree: `--lib` 40 passed, `--test qualification` 4 passed.
- Charter row: `GOAL.md` "coordinator fails before, during, or after local
  commits — Retry with the same client-request ULID is safe; the result has
  a typed partial outcome, no duplicate apply, and no election."
- Gap record: `docs/evidence/qualification-gap-sweep.md` row 9 (partial):
  coordinator "failure" was modeled only by the idempotent-retry path; no
  crash injection existed at the cluster seam.

## The lane's construction

Topology: the production RF=2 shape — two Regions (`eu-1`, `us-1`), two
seats each (`member-a`/`member-b`, `member-c`/`member-d`), so every
regional quorum is exactly both distinct Members and the global threshold
is both Regions. The cluster is created through the real seam
(`ClusterHost::create`), which lays the immutable identity and the four
durable member roots.

Crash injection: `ClusterHost::client_write` has no injection point (the
gap's own words). The lane therefore drives the coordinator path the seam
drives — `coordinate_composed_write` and the per-member `accept_put` it
fans out to, over the cluster's durable member roots in the host's
canonical order — and stops at one boundary. The loss itself is the
kill-point discipline of `src/storage/compaction_tests.rs`: drop
everything without shutdown; only facts committed at
`LocalDurability::Synced` survive (the storage layer syncs the published
pointer before any `Synced` claim). The retry is then served by the real
seam: `ClusterHost::open` over the surviving roots, `client_write` at a
healthy member, with the SAME operation identity (the idempotency
identity the charter's client-request ULID names; the operation text is
its stand-in until ADR 0027 lands).

## The boundary table

Per-boundary asserted facts, every row on real durable members:

| # | Boundary (path order) | Durable facts kept at loss | Crashed attempt's observable outcome | Retry, same identity, healthy member | No-duplicate-apply evidence | No-election evidence |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | validation — schema admission per member (`modes::admit`) before any engine work | none | the boundary's refusal is typed (`SchemaAdmission`) with zero durable effect; a mid-validation loss answers nothing — the client's outcome is unknown | committed once; a later write still wins (nothing wedged) | every member's storage sequence advances exactly once (0→1); the duplicate delivery freezes all sequences | retrying member coordinated the retry itself; epoch stays 1 |
| 2 | identity mint — the operation identity exists and is held by the client; no engine holds it | none | unknown (no reply) | committed; the MINTED identity lands; contributor sequences identical across two deliveries | sequences advance once; second delivery replay is byte-identical facts | same |
| 3 | first-member WAL append — the coordinator's own seat (engine commit at `Synced` + own-peer CRDT op + staged snapshot) | one seat (`member-a`) | **typed partial**: `QuorumNotReached` naming the stored contributor and its recorded sequence — never silent success, never a fresh identity | committed through `member-b`; the crashed seat never re-applies | crashed seat's sequence frozen at its recorded value; the receipt carries that same recorded sequence; other seats advance once | same |
| 4 | second-member WAL append — the RF=2 regional boundary is durable, quorum not yet evaluated | both `eu-1` seats | unknown (no reply); a coordinator seeing only `eu-1` resolves the SAME request committed from the recorded receipts | committed through `member-c` (the OTHER Region); `eu-1` seats frozen | `eu-1` contributor sequences equal the recorded ones; zero new regional applies | same |
| 5 | quorum assembly — contributor counting against the regional/global thresholds | all four seats (global fan-out complete) | unknown (no reply) | committed at `GlobalQuorum` by **pure replay** | zero new applies anywhere; all four sequences frozen | same |
| 6 | materialization/publication — receipt built; every engine already published its commit; state served | all four seats + the assembled receipt (lost with the coordinator) | unknown to the client; the write is published on every member | committed; identical operation, level, epoch, and per-member sequences to the lost receipt | the identical per-member facts answer, not new ones | same |
| 7 | reply — the receipt reached the client | all four seats + client-held receipt | committed (the client knows) | the at-least-once re-delivery replays the recorded receipt forever | all sequences frozen; recorded receipt facts identical | re-delivery coordinated at a different member; epoch stays 1 |

## The typed-outcome lattice (cluster crate's durability ledger)

`the_typed_outcome_lattice_classifies_every_crash_boundary_at_the_ledger`
maps each boundary class onto `crates/terrabase-cluster/src/durability.rs`
typed outcomes: nothing stored → `DurabilityFailure::Timeout` (the typed
unknown, distinct from partial, never durable); one seat stored →
`Partial` naming the stored seat and the missing one; the boundary met
before evaluation → a committed receipt from the recorded facts. The same
identity recovers through another member without ownership
(`recovered.coordinator()` is the retrying member), and a duplicate
acknowledgement is refused typed (`DuplicateAcknowledgement`) — it never
counts twice.

## No leader, lease, or redirect

Three legs, every boundary: (a) the retry's `accepting_member` is the
retrying member itself — no handoff step exists; (b) the epoch and
membership are unchanged across every crash/retry (no election bumped
anything); (c) `the_seam_refusal_vocabulary_names_no_authority_and_never_redirects`
enumerates every `ClusterWriteRefusal` and `ComposedCoordinationError`
variant — the match is exhaustive, so adding a variant fails this file's
compilation — and no variant's name or payload contains leader, lease,
redirect, election, term, primary, or master; an unseated writer is
refused typed (`UnknownMember`), never redirected.

## Facts asserted (consolidated)

- Every boundary resolves through the same-identity retry into exactly
  the outcome its position implies: rows 1, 2, 4, 5, 6 commit (4 and 5
  with zero new applies at already-stored seats), row 3 additionally
  proves the typed partial with the durable contributor named, and the
  client-side unknown class is grounded in the ledger's `Timeout`.
- No duplicate logical apply anywhere: per-member published storage
  sequences advance exactly once per operation identity; every later
  delivery replays the recorded receipts (the storage layer resolves a
  retried identity to its recorded sequence, never a new commit), and
  every member serves the one converged body.
- No election, lease, leader, or redirect: as above.

## Honest limits

- Deterministic in-process lane, one run per boundary at `d1477b2`. The
  crash is a drop-without-shutdown over `Synced`-committed state — a
  process-kill model, not a real kill of a serving host mid-request; the
  transport-level seam (`terrabase-tbep`) is not exercised here.
- The injection point is the coordinator path itself
  (`coordinate_composed_write` / `accept_put`) because
  `ClusterHost::client_write` is atomic; the retry, reads, and
  reopen run through the real `ClusterHost` seam. A host-level injection
  hook would remove this distinction.
- A duplicate delivery appends an equivalent own-peer CRDT op (fresh dot)
  at seats that already held the write — CRDT delivery is at-least-once
  and converges. The exactly-once fact this lane proves is at the
  durable-application plane: the storage sequence advances once per
  identity and the recorded receipt replays.
- The idempotency identity is the operation text; the charter's
  client-request ULID (ADR 0027) is unimplemented (sweep constraint 3).
- Concurrent lanes were live at run time: the first full `cargo test -p
  terrabase-cluster` attempt did not compile because another lane's
  in-flight `tests/model_checker.rs` (not this packet's file) carried a
  compile error mid-edit; after that lane fixed its file, the full crate
  run on the same tree passed every target — 63 tests total (lib 40,
  coordinator_crash_matrix 9, global_durable_loss 5, member_loss_after_ack
  2, model_checker 3, qualification 4). The 9/9 crash-matrix result is
  this packet's evidence; CI owns the full gate.

## What this closes and what remains

Row 9's named gap — "the before/during/after-every-durable-boundary
matrix ... is unrun" — is now run at the in-process cluster seam, with
the boundary table above as its receipt. The row stays lane-scoped:
production-scope crash injection (real process loss mid-request at the
serving host, with real client retries) and the ULID-based identity
remain unrun, and no row in this repository is closed at production
scope by an in-process lane.
