# BK-04 scheduling, encryption, compression, retention, and destinations

Status: implemented module record for BK-04
(`TerraBaseV2#45`), 2026-08-26; revised in repair loop 1
(2026-08-26) so retention reclaims sealed bundle artifacts. Authority:
workspace charter
PD-2026-08-26-11 (D2 agent-drivable typed operations), the W4 transfer
contract's public-type ownership, and the BK-01 backup core this module
drives. No parity row is resolved by this record alone: PB-6011..PB-6014
stay `unresolved` until the parity overlays (integrator-owned) cite the
focused tests here as Proof IDs, and every claim below is scoped to
focused, in-crate evidence — no Recovery Receipt, no performance claim, no
cryptographic-strength claim.

## What landed

All code is in `src/backup/schedule/`, test-first at the module's public
seam (the red contract suite landed before any implementation, as a
compile-failing `cargo test --lib` at the seam):

- **Scheduling (PB-6011).** `ScheduleDriver` is a deterministic state
  machine over the public `run_backup` seam, driven by caller-supplied
  ticks. The engine runs no timers — Platform-owned task controls
  disposition to the Platform — so the backend (or an agent through it)
  owns both the tick stream and the durability of `ScheduleState`, a
  plain value handed to every operation. Cadence: a full interval plus an
  optional incremental interval; the first run is always a full;
  incrementals chain on the newest sealed point's boundary until the full
  interval rotates the chain; a due incremental with no new journal since
  that boundary is a recorded no-op that rearms its window. Missed-run
  policy: `RunOneNow` executes a late boundary immediately and re-anchors
  at completion time; `SkipUntilNextWindow` never backfills — fully
  expired windows are absorbed by exact grid multiples and execution
  happens only at grid boundaries. Pause and resume are typed operations
  that leave anchors untouched. Status is a typed read of phase, cadence,
  the newest completed runs, counts, and the last typed failure — never a
  destination path, key material, or archive content.
- **Destinations (PB-6012, local kind).** `DestinationConfig` is a typed
  value with side-effect-free `validate()` (empty, relative, `..`-bearing,
  filesystem-root, and file-at-root rejections, each typed) and a
  `probe()` writability proof (create, unique probe file, remove). Remote
  kinds are future typed variants with their own validation, never a
  generic URL/credential escape hatch.
- **Encryption and compression (PB-6013).** `seal_artifact_bundle`
  derives one `bundle-<identity>.tbv2b` per sealed recovery point holding
  the manifest text plus every archive member, framed with lengths and
  SHA-256 digests, optionally LZSS-compressed, optionally encrypted.
  Encryption is encrypt-then-MAC: HKDF (HMAC-SHA256 extract-and-expand)
  derives domain-separated subkeys; the frame stream is masked with a
  SHA-256 counter-mode keystream; an HMAC-SHA256 tag covers the whole
  container. The construction is an in-crate composition over the backup
  family's own SHA-256 — the workspace forbids external dependencies —
  and claims nothing about vetted-cipher strength. Opening is fail-closed
  in a fixed order (structure, authentication, identity binding,
  decryption, decompression, per-member digest): a wrong key and a
  tampered byte are the same single typed failure and the open path never
  discloses which check failed. `extract_artifact_bundle` rebuilds a
  complete, loadable recovery point only with the right key. Keys are
  caller-supplied per operation, never stored, zeroed on drop, redacted
  in Debug, and never enter artifact bytes (only HKDF subkeys touch the
  container).
- **Retention (PB-6014).** `enforce_retention` is a standalone typed
  operation: it discovers sealed points, groups them into chains by base
  links, protects the newest chain outright, keeps every chain younger
  than the policy's minimum age, and prunes whole expired chains oldest
  first, receipting each pruned point (identity, kind, boundary). A chain
  root is never pruned without its incrementals and incrementals are
  never stranded without their root. Age is the root full's manifest
  mtime compared against a caller-injected `now`; unreadable or future
  sealed-at times fail the sweep typed rather than prune on a guess.
  Pruning a point reclaims **every** artifact of it: the `rp-<identity>`
  directory and the `bundle-<identity>.tbv2b` artifact a scheduled run
  seals beside it, so pruned data is unrecoverable rather than merely
  hidden behind a surviving bundle and orphaned bundles cannot grow
  unbounded. The protected newest chain keeps its bundles like the rest
  of its artifacts. Deletion is idempotent (an already-missing directory
  or bundle counts as pruned), and a failure mid-sweep returns the typed
  cause plus the partial receipt.

## Focused evidence

In-crate suite `src/backup/schedule/tests.rs` (28 tests, all green under
the pinned toolchain): destination validation and probe; compression
round-trip over empty, tiny, repetitive, pseudo-random, and multi-window
corpora plus malformed-stream rejection; encrypted-bundle open-with-right-
key-only; tamper-anywhere fail-closed; extraction into verifiable recovery
points with wrong-key denial leaving no partials; keyless compressed
bundles; per-seal nonce freshness with both seals opening; key-material
absence from artifacts and Debug output; retention keeping the only chain,
pruning whole expired chains with receipts while keeping young ones,
pruning the sealed bundle of every pruned point (with the kept chain's
bundles surviving and idempotent re-sweeps), keeping the newest chain's
bundles whatever their age, keeping sub-minimum-age chains, failing closed
on an invalid manifest, and pruning nothing when disabled; the
scheduler's first-full requirement,
incremental chaining and rotation, no-op rearm, both missed-run policies
(with the same state contrasting them), pause/resume, typed construction
failures, typed run-failure recording after external destination damage,
encrypted schedules sealing per-run bundles without secrets in status,
post-run retention enforcement, and a rotating full whose sweep reclaims
the rotated-out chain's directory and bundle together.

Public-seam integration `tests/recovery/schedule_public_chain.rs`
(2 tests): a first full anchors the destination, an incremental over the
anchored boundary appends as a chain link, a retried cadence slot is
idempotent (no duplicate recovery point), and an unanchored link fails
`ChainIncomplete` sealing nothing. `TenantScope` has no public
constructor by design, so data-bearing cadence behavior is in-crate.

## Limits and dispositions

- **Lane fixtures.** The acceptance criterion's "scheduled full+incremental
  chains run in a lane fixture" is BK-02's exclusive recovery lane; the
  focused suites here prove the same behavior without lane claims.
- **At-rest encryption of plaintext points.** A bundle is an authenticated
  artifact derived from a sealed point; the plaintext `rp-` directory the
  BK-01 writer publishes stays beside it. Encrypting the point directories
  themselves is archive-writer territory (BK-01's files) and remains open.
- **Platform-owned task controls.** Timer ownership, task lifecycle, and
  scheduling authorization disposition to the Platform; the engine exposes
  only the typed operations above (PD-2026-08-26-11 D1/D2).
- **Remote destination kinds.** Object-store and other providers are
  future typed `DestinationKind` variants, each independently qualified
  per PB-6012; no generic HTTP escape hatch will exist.
- **Memory bounds.** Sealing and extraction materialize one point's
  members in memory, the same honest bound the BK-01 writer discloses for
  state archives. The retention sweep holds only manifests and directory
  paths.
- **Clock domains.** Cadence is caller ticks; retention ages are the
  destination's wall clock (manifest mtimes). The scheduler's per-run
  retention sweep reads `SystemTime::now()` at the sweep and records only
  counts in state.
- **Skip-policy granularity.** `SkipUntilNextWindow` executes only when a
  driven tick lands exactly on a grid boundary; a caller that ticks
  irregularly under that policy executes at the first boundary it lands
  on or after. This is documented behavior, not a timer.

## Integrator collision actions

- `src/backup/mod.rs` is integrator-owned: expose this module's typed
  surface (a `pub use schedule::{...}` line or `pub mod schedule`) so the
  backend can drive it; until then the module is crate-private and
  `#![allow(dead_code)]` stays at its root.
- `tests/recovery/main.rs` gained one `mod schedule_public_chain;` line
  (the designed per-file coordination point of that test binary).
- Parity overlays for PB-6011..PB-6014 (and PB-6017 where the scheduler
  maps its causes) stay integrator-owned; this record's test names are the
  intended Proof IDs.
