# Four-combination storage-tier matrix lane (in-process receipt)

Status: recorded 2026-08-29. The lane is
`src/storage/lsm/tier_matrix_tests.rs` (wired as a `#[cfg(test)]` module of
the LSM suite) — 3 rows, green under `cargo test -p terrabase-v2 --lib
tier_matrix`, run at develop `3d45d96` plus this lane's files. This
receipt closes the matrix half of gap-sweep row 4
(`docs/evidence/qualification-gap-sweep.md`); the sweep file itself is not
edited by this landing and its row should be re-scored by its owner.

## Scenario

GOAL.md "Active storage tiers": the supported profiles are Tier 2 only,
Tier 1 plus Tier 2, Tier 2 plus Tier 3, and all three tiers; Tier 1 and
Tier 3 are independently optional, and "their absence is not degraded
health and produces no warning, fallback delay, empty result, or repeated
background work." Before this lane, `src/storage/lsm/tier.rs` proved each
tier behavior in isolation (13 rows: mover demote/serve, dry run,
exhaustion, checksum-source fallback, the §4 failure table) and
`src/tiers.rs` proved the typed administration gates — but no test named
the four-combination matrix or asserted identical logical state across
it.

## Lane design

One shared logical operation sequence runs under all four profiles
(`MATRIX`: `tier2-only`, `tier1+2`, `tier2+3`, `tier1+2+3`), each in a
fresh LSM location with the same tuning:

1. 24 seed documents, one overwrite, one delete;
2. fingerprint A: the materialized tenant state (`documents_at`), every
   document read individually (`read_document_in_collection_at`), and the
   canonical published state (`published_state`: sequence watermark +
   root-chain head over committed digests);
3. identical mover pressure in every profile (the same data volume
   against a one-byte NVMe budget): Tier-3 profiles demote up to two
   segments through the real mover into the engine's real cold store
   (`<location>/tier-objects`, verified objects, manifest epoch
   advanced); Tier-3-disabled profiles attempt the same pressure three
   times and must show zero work;
4. fingerprint B: the full read-back — in Tier-3 profiles this is the
   engine's real cold path (`collect_range` through
   `read_cold_segment_entries`);
5. twice-repeated cold reads through the profile's read order (Tier-1
   cache first when enabled, then `read_segment_with_sources`), plus one
   read of a still-local segment;
6. post-cold writes (8 new documents, an overwrite of a demoted-range
   document, a delete of one), fingerprint C;
7. a quiescence sample (no operations between two counter readings);
8. reopen and re-fingerprint (Tier-3 profiles recover through
   `rebuild_indexes_from_cold_tier`; the others from local segments
   alone).

Every optional tier gets a counting seat (`TierSeat` wrapping
`LocalObjectStore`) constructed in every profile — a disabled seat opens
no directory, so a lane that wrongly addressed it would leave countable
calls and artifacts rather than taking an unreachable branch.

## What is proven

- **Identical logical and canonical state across the matrix.** All four
  profiles produce byte-identical fingerprints at A, B, C, and after
  reopen: the same 30 live documents (24 seed − 2 deleted + 8 post-cold)
  in canonical order, the same per-document reads (both deletes read
  `None`, both overwrites carry their final version), the same published
  sequence, and the same root-chain identity. Moving bytes between tiers
  — or having no optional tier at all — changes nothing about what was
  committed. The demoted segments are the same segments, byte for byte,
  in both Tier-3 profiles, and stay resident on the mandatory local tier
  in the Tier-3-disabled profiles: only the seat of the bytes differs.
- **Disabled tiers are skipped structurally.** With Tier 3 disabled, the
  identical pressure produces zero store calls, no `TIERMANIFEST`, no
  `tier-objects` directory, `tier_snapshot()` at epoch 0 with zero
  objects, and an unchanged segment set — three attempts in a row change
  nothing (the impossible demotion is not attempted, much less retried).
  With Tier 1 disabled, the cache seat records zero calls and zero files
  even while the same cold reads are served. Positive controls keep the
  zero rows from being vacuous: enabled profiles show real demotions,
  real cold reads, and (in `tier1+2+3`) real cache misses, fills, and
  hits.
- **Bounded, single-pass work.** One demotion is exactly one object
  `put` (no retry loop); the first cold-read pass costs one consultation
  of the cheapest enabled seat per segment; the second pass is one cache
  consultation and nothing else (zero cold-tier reads, zero re-fills)
  when the cache exists, or exactly one cold read per segment without
  one; a still-local segment reads through no optional seat at all; no
  seat ever sees an `exists` probe or a `delete`; and no seat grows
  between two samples with no operations in between. There is no
  background mover loop in this landing — runs are caller-driven and
  `src/tiers.rs` owns the typed unconfigured/paused refusals.
- **Intentional disable is not degraded health.** The disabled profiles
  serve exactly the final state the enabled profiles serve; no read
  returns an empty or partial result (every fingerprint equals the
  four-way reference, and the live count is asserted per profile);
  `journal_divergence` is 0 everywhere; and the data stays resident on
  the mandatory tier (`nvme_bytes > 0`).
- **No warning can be emitted on this seam.** The tier surfaces are
  typed outcomes only and `src/storage/` carries no logging channel
  (verified: no `tracing`/`log` in the storage tree). "No fallback
  delay" is proven structurally — a skipped tier contributes zero calls
  to a synchronous, bounded read chain (the crate's own source cap is
  `MAX_COLD_SOURCES = 8`), never a timeout — because timing rows on this
  shared host stay honestly inconclusive.

## Commands and results

- `cargo test -p terrabase-v2 --lib tier_matrix` — 3 passed, 0 failed
  (`the_four_combination_matrix_yields_identical_logical_and_canonical_state`,
  `disabled_tiers_are_never_addressed_and_are_not_degraded_health`,
  `tier_work_is_bounded_single_pass_and_never_retries`).
- `cargo test -p terrabase-v2 --lib storage::lsm::tier` — 15 passed, 0
  failed (the prior 12 tier rows plus this lane's 3).
- `cargo clippy -p terrabase-v2 --all-targets --locked` — no findings in
  this lane's files. (Two warnings exist elsewhere at this develop head —
  `src/query/tests.rs:501` and `src/replication/tests.rs:2047` — owned by
  other lanes and untouched here.)
- `rustfmt --edition 2024 --check src/storage/lsm/tier_matrix_tests.rs`
  — clean.

## Honest limits

- **Tier 1 is a lane-composed seat, not a shipped device.** The engine
  has no distinct configurable Tier-1 tier (the gap sweep names exactly
  this hole); the lane composes the Tier-1 cache over the same
  `ObjectStore` seam the mover uses, first in the cold-read order.
  Combinations containing Tier 1 prove the composition contract — the
  cache changes where cold bytes come from, never the logical or
  canonical state — not a production NVMe cache tier. A shipped Tier-1
  device remains open on row 4.
- **Tier 2 is the LSM location.** A local temp directory stands in for
  network block storage, the same standing as every other LSM lane; the
  Tier-2-only production posture (hot-path SLOs on real Block Volume,
  Ampere hardware) is gap row 3 and stays blocked-external.
- **Call-count coverage is per seam, not per syscall.** The mover and
  the lane's read chain are fully counted (they run through the counting
  seats). The engine's own document reads construct their internal
  store handle, so their cold-tier activity is proven by results and
  artifact counts (object files, manifest objects, `tier_snapshot()`),
  not by intercepted calls.
- **No live object store.** `LocalObjectStore` stands in for the S3
  class, as on every prior tier receipt; live S3 failure classes remain
  recorded unrun (row 5).
- **"No delay" is structural, not measured.** Zero-call skipping plus a
  synchronous bounded chain is the proof altitude available on this
  shared host; no latency number is claimed.
