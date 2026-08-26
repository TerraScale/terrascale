# SE-02 spike evidence: Loro prototype receipts

An identical copy sits at `evidence/spike/se02-prototype-evidence.md`;
the repository's evidence policy keeps `evidence/*` untracked until the
integrator publishes reviewed receipts, so this tracked copy is the
packet's canonical record.

No production, performance, or multi-region claim: prototype-scoped
bytes-on-wire observations under PD-2026-08-26-13 D1 (the seed of
SE-07's budgets) and focused test outcomes at the named source identity.
No Benchmark Receipt exists for anything here; local timings were not
taken and are not cited.

- Source identity: worktree `TerraBaseV2/.worktrees/terrabasev2-se02`,
  branch `fleet/se02`, base `develop` @ `431841a`, plus this packet's
  commit (see the branch tip).
- Base engine: `loro = 1.13.9` (cargo registry cache,
  `index.crates.io-1949cf8c6b5b557f`), MIT.
- Toolchain: pinned `1.97.1` (`rust-toolchain.toml`), Linux
  x86_64. All cargo commands ran with `--offline --locked`.
- All data synthetic (fixed seeds in `spike/sync-core/src/scenario.rs`:
  `0xD1CE`, `0xFEED`, `0xB057`, `0x0DD0`). No tenant, credential,
  customer path, or document body from any real system.

## Commands and outcomes

| Command (cwd) | Outcome |
| --- | --- |
| `cargo check --offline --locked` (`spike/sync-core`) | pass |
| `cargo test --offline --locked` (`spike/sync-core`) | pass: 4 unit + 10 contract tests |
| `cargo clippy --offline --locked --all-targets -- -D warnings` (`spike/sync-core`) | pass |
| `cargo run --offline --locked --release --bin se02_spike` (`spike/sync-core`) | pass; measurements below |
| `cargo test --offline --locked` (`crates/terrabase-sync-core`) — first run against the contract tests with an empty lib | red: 17 errors, all unresolved contract items (`SyncCore`, `SyncCheckpoint`, `SyncFrame`, …) — the contract stated before implementation |
| `cargo test --offline --locked` (`crates/terrabase-sync-core`) — after implementation | pass: 2 unit + 10 contract tests |
| `cargo clippy --offline --locked --all-targets -- -D warnings` (`crates/terrabase-sync-core`) | pass |
| `cargo doc --offline --locked --no-deps` (`crates/terrabase-sync-core`) | pass |
| `cargo fmt` + `cargo check --offline --locked` + `cargo clippy --offline --locked --all-targets -- -D warnings` + `cargo test --offline --locked` (engine workspace root) | pass — engine workspace untouched by this packet; full suite green including the 23-name W4 replication contract suite and the streaming suite |

### Unrun, with reasons

- `cargo deny check` over the seam/spike closure: cargo-deny is not
  installed in this environment and global tools are not installed by
  builders. The manual license inventory (all 157 packages, resolved
  from each manifest in the registry cache) is in
  `docs/spike/se02-base-engine-spike.md`.
- `wasm32-unknown-unknown` build of the seam crate: target not installed
  (`rustup target list --installed` shows only `x86_64-unknown-linux-gnu`);
  also removed as a requirement by the fifth spec revision
  (PD-2026-08-26-12 removed client embeddings).
- In-process allocation/copy accounting on the hot path: would require a
  tracking global allocator (unsafe) or a profiler, neither of which
  belongs in the spike; recorded as unmeasured, not as met.

## Proof 1 — delta delivery (representative change patterns)

64 documents × 24 fields; consumer in sync; one change measured per
pattern (release build, one run):

| Pattern | Full-document re-send (B) | Loro delta (B) | Delta/commit | Ratio |
| --- | --- | --- | --- | --- |
| single-field patch | 551 | 245 | 1 | 0.445 |
| whole-document replace | 551 | 681 | 1 | 1.236 |
| delete (tombstone) | 551 | 94 | 1 | 0.171 |

Initial sync of the scope: full snapshot 70_532 B vs 35_264 B for the
plain bodies (snapshot carries structure and history; a state-only
export exists for the byte-sensitive initial-sync path — SE-07 input).

Asserted by `delta_delivery_beats_full_document_resends_for_field_patches`
and `whole_document_replace_delta_is_comparable_to_the_full_body`.

## Proof 2 — checkpoint resume after reconnect

96 commits before disconnect, 49 while disconnected → resume from the
recorded checkpoint served 1 batch / 2_526 B; consumer converged to the
server state; a duplicate re-delivery of the final batch stayed converged
(at-least-once). A second consumer stalled past the 4_096-commit
retention window was answered with the retained-floor outcome and
converged by snapshot resync; deltas served again afterwards (UpToDate).

Asserted by `checkpoint_resume_after_reconnect_converges_and_survives_duplicates`
and `consumer_behind_the_retained_floor_resyncs_by_snapshot`.

## Proof 3 — bounded batch assembly under burst

50_000 commits (single-field patches over 512 documents × 24 fields)
materialized in 500-commit chunks while an attached consumer drained
through 65_536-B-budget batches:

- 200 batches, 2_508_922 B total (≈50 B per delivered change), max batch
  24_696 B, every batch within budget (`all_batches_bounded = true`); the
  consumer converged and survived a duplicate final batch.
- Retained after the burst: 124_064 Loro ops.
- Resident-set observations (coarse, whole-process `/proc/self/statm`
  samples; allocator retention included; the free APIs did not return
  pages to the OS in this run): before 7_634_944 B; after materialize
  37_486_592 B; after drain 37_584_896 B; after `free_history_cache` +
  `free_diff_calculator` + `compact_change_store` 37_584_896 B (no
  observed reclaim). These are observations, not bounds and not claims.

Asserted by `burst_delivery_stays_within_the_byte_budget`.

## Proof 4 — deterministic replay

Two servers fed the same journal produced byte-identical snapshots and
identical deep values; delta-wise, snapshot-wise, and commit-by-commit
consumers observed identical document sets; the drained consumer's own
snapshot was byte-identical to the server's.

Asserted by `replay_is_deterministic_and_consumers_agree` (spike) and
`replay_is_deterministic_at_the_seam` (seam crate).

## Seam crate contract outcomes (`crates/terrabase-sync-core`)

Materialization and checkpoint-served deltas; whole-body put replace and
tombstone delete; byte-budget conformance under a 2_048-B budget across
400 feed-and-drain commits; retained-floor resync; versioned checkpoint
encoding failing closed on unknown versions; typed errors for
out-of-order commits, foreign-peer checkpoints, and ahead-of-server
checkpoints; frame seal/verify/wire round-trip with tamper detection;
idempotent duplicate delivery; deterministic frame digests.

## Limits

- One machine, one run per scenario, debug profile for tests and release
  for the measurement bin; no statistical gates, no baselines — this is
  not benchmark evidence and supports no speed statement.
- The resident-memory samples are process-level and include everything
  in the process; they bound nothing.
- Batch acceptance uses a conservative sum-of-single-commits estimate:
  observed under-fill (max 24_696 B against the 65_536 B budget) is a
  packing-tightness observation for SE-07, not a defect.
- The 23-name W4 replication contract suite and the streaming suite were
  executed green at this packet's engine-workspace state; this packet
  does not modify engine source.
