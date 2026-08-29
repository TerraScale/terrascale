# ADR 0023 native replication golden vectors

Status: recorded 2026-08-29. The vectors are
`crates/terrabase-replication-core/tests/adr0023_golden_vectors.rs` (6
tests, all green) plus the checked-in fixtures under
`crates/terrabase-replication-core/tests/vectors/adr0023/`. Focused
command:

```text
cargo test -p terrabase-replication-core --test adr0023_golden_vectors
cargo test -p terrabase-replication-core
```

Full crate result at recording time: 6 golden tests pass and all 47
pre-existing tests in the crate (unit, `anti_entropy`,
`composed_member`, `leaderless_convergence`) still pass.

## What is pinned

Every pin asserts a literal hex string in the test AND requires the same
hex to appear in the matching JSON fixture, following the
`terrabase-crdt` register-vector pattern
(`crates/terrabase-crdt/tests/register_golden.rs`). The fixture label
`terrabase-native/1` names the native codec family; on the wire that
major is the first byte `0x01` of every pinned encoding
(`EXCHANGE_CODEC_VERSION = 1` for exchange/snapshot forms,
`terrabase-causal`/`terrabase-crdt` `CODEC_VERSION = 1` for causal and
register forms).

| # | Structure | Type and bytes pinned | Fixture |
| --- | --- | --- | --- |
| 1 | Replication frame (full) | `ReplicaExchange::Delta` from `ReplicaDoc::delta_for` — codec major, op count, 5 ops in canonical dot order (2 actors: 4 authored ops + 1 absorbed op; put, delete, and a whole-body replace that drops a field) | `replication-frame-full-delta-batch.json` |
| 2 | Replication frame (interval-selected) | same replica, remote frontier covering member 7 counters 1..=2 — exactly the 3 missing ops travel | `replication-frame-partial-delta-batch.json` |
| 3 | Contiguous + received-interval progress summary | `CausalContext` canonical bytes (the wire form `ReplicaFrontier` exchanges): actor 0x11 prefix 3 + intervals 5..=7, 10..=12; actor 0x22 prefix 4 | `progress-summary-causal-context.json` |
| 4 | Acknowledgement range structure | `StableFrontier` canonical bytes from required attestations: prefixes 5/4/0 — sparse interval above a prefix and an absent required member contribute nothing (ADR 0023 retention rule) | `stable-frontier-acknowledgement.json` |
| 5 | Anti-entropy causal summary | `anti_entropy::causal_hash` BLAKE3 (domain `anti-entropy-causal-state`) over the #3 summary bytes; `anti_entropy::materialized_hash` (domain `anti-entropy-materialized-state`); dual `MerkleRoots` from `tree_for_replica` over the #1 replica at bounds (2, 64) | `anti-entropy-causal-summary.json` |
| 6 | Durable replica snapshot | `ReplicaDoc::snapshot_bytes` — actor, sequencer, clock, retained floor, per-document LWW registers in the `terrabase-crdt` native register codec, then the same op encoding as the frame | `replica-snapshot.json` |

Each test also pins behavior beyond the bytes: the full frame decodes
(`absorb`) into a fresh replica that re-exports the identical bytes and
materializes the same state; the partial frame alone converges a
covered replica to the same materialized state; the progress summary
encodes identically when the same dot set is assembled one counter at a
time in shuffled order with actors inserted in the opposite order
(canonical normalization); summaries and acknowledgements round-trip
through `decode_canonical`; the snapshot restores (`from_snapshot`) to
an equal frontier and materialized state and its operation tail is
byte-identical to the full frame's operation section.

## Architecture independence

Every pinned value is produced only from: fixed-width `u64` integers
(minimal LEB128 or big-endian), fixed 16-byte replica identities,
`BTree`-ordered collections, exact IEEE-754 bit patterns for numbers,
and BLAKE3 over those bytes. No `usize` width, pointer size, wall
clock, hash-map iteration order, or platform float formatting
participates in any pinned encoding, so the same vectors hold on
`x86_64-unknown-linux-gnu` and `aarch64-unknown-linux-gnu` and across
mixed replica sets. (Execution in this lane was on x86_64 only — see
limits.)

## Honest limits — what is NOT pinned

- **The in-memory progress/ack types have no bytes to pin.**
  `progress::ReplicationProgress`, `DurableProgressTracker`,
  `DurableAcknowledgement`, `ReplicationCursor`, `RetentionGuard`, and
  `anti_entropy::VerifiedFrontier`/`VerifiedStableFrontier` are
  decision structures with private fields and no canonical encoder in
  the crate today. Vector #4 therefore pins the byte-level
  stable-frontier acknowledgement that this crate's dependency
  `terrabase-causal` does define (`StableFrontier` canonical bytes);
  it is the only ack-shaped structure in this crate's dependency tree
  with a canonical encoding. When a wire form for
  `DurableAcknowledgement` (member, generation, prefix, gap ranges)
  lands, it needs its own golden vectors.
- **The ADR 0023 session message envelopes are not implemented in this
  crate.** `ConnectionHello`, `PartitionOpen`, `ProgressSummary`,
  `NeedIntervals`, `CommitBatch` envelope, `ProgressAck`,
  `RepairRequired`, snapshot-offer/chunk/commit, Merkle
  probe/range messages, `SessionError` — with session ULIDs, strictly
  monotone message sequences, payload hashes, and replica-set epochs —
  do not exist as types in `terrabase-replication-core`; the crate
  exchanges `ReplicaExchange` delta batches and snapshots. No QUIC
  framing, compression, or envelope header bytes are pinned here.
- **ADR 0022 canonical commits are out of scope here.** The pinned ops
  are this crate's `ReplicaOp` batches, not ADR 0022 canonical commit
  records; register-level native codec vectors already live in
  `terrabase-crdt` (`tests/register_golden.rs`, `vectors/registers/`).
- **No signed `PartitionSnapshotManifest` exists in code.** Vector #6
  pins `ReplicaDoc::snapshot_bytes` only — the replica staging form,
  not a signed manifest with schema digests or bucket-range
  generations.
- **Merkle roots are pinned at named bounds.** The dual roots in
  vector #5 are functions of the tree bounds (leaf entries 2, max
  leaves 64); a different bound layout is a different root. Logical
  leaf hashes are stable across physical layout, but only the named
  bounds are pinned.
- **Cross-architecture execution not yet run.** The vectors are
  architecture-independent by construction (argument above), but this
  lane executed them on x86_64 only; no aarch64 (or mixed-architecture
  process-pair) run has been recorded.

## Reproducing

```text
cd TerraBaseV2
cargo test -p terrabase-replication-core --test adr0023_golden_vectors
```

The fixture replica is fixed: member 7 authors puts at 1000/1100 ms, a
delete at 1200 ms, and a whole-body replace at 1300 ms; member 8's put
at 1050 ms is absorbed. Changing any authored time, document, field,
or actor derivation changes the pinned bytes by design — that is the
tripwire.
