# SE-02 decision record: Loro as the sync core — GO

Status: builder decision record for the SE-02 spike (ticket #4). Not an
ADR: acceptance is the parent's, and this record resolves no parity row
and makes no performance claim (PD-2026-08-26-03; PD-2026-08-26-05 D5:
stack choices are engineering decisions, not readiness or performance
claims).

## Verdict

**GO**: adopt Loro 1.13.9 as the base engine of the client-channel sync
core, wrapped by the `terrabase-sync-core` seam crate
(`crates/terrabase-sync-core/`), with the conditions in
[Conditions on the GO](#conditions-on-the-go). The native extraction
fallback (generalizing the W4 member and streaming state machines) is
recorded below as plan B with its own budget, and stays the escalation
path if any condition fails.

What GO means here, exactly: the spike's four proofs passed at prototype
level with focused tests green, the license audit found no contaminating
dependency, and the seam crate exists with a test-first typed
frame/checkpoint contract compiling native with `deny(unsafe_code)`.
Nothing here is a `qualified` or benchmarked status: sync-path
performance budgets are SE-07's to write and measure, and SE-15's to
baseline.

## What was built

| Artifact | Path | What it is |
| --- | --- | --- |
| Spike prototype | `spike/sync-core/` (standalone workspace crate) | Journal-to-Loro materialization, delta delivery with byte budgets, checkpoint resume, burst behavior, determinism; a bin prints the measured outcomes; 4 unit + 10 contract tests. |
| Seam crate (the GO deliverable) | `crates/terrabase-sync-core/` (standalone workspace crate) | `SyncCore` (apply/delta_since/resync_snapshot/compact_history), `SyncCheckpoint` (versioned, fails closed), `SyncFrame` (correlation/causation/content digest, wire encoding); red tests first, then implementation; `deny(unsafe_code)`, `deny(missing_docs)`; 2 unit + 11 contract tests. |
| V1 conflict mapping | `docs/spike/se02-v1-conflict-mapping.md` | Draft mapping citing the pinned V1 commit. |
| Evidence receipt | `docs/spike/se02-prototype-evidence.md` | Exact commands, outcomes, measurements, unrun items. A copy sits at `evidence/spike/se02-prototype-evidence.md`, which the repository's evidence policy keeps untracked until the integrator publishes reviewed receipts (the W4-CA-12 precedent). |

Neither crate is a member of the engine workspace: both manifests carry
their own empty `[workspace]` table, so the engine build (zero external
dependencies) is untouched and the zero-dependency rule for engine core
modules holds. Wiring `crates/terrabase-sync-core` into the engine
workspace is an integrator action (below).

## License and dependency audit

- `loro = 1.13.9` is MIT (crate manifest `license = "MIT"`, registry
  cache identity `index.crates.io-…/loro-1.13.9`).
- The full transitive closure of the seam/spike lockfiles is 157
  packages (identical set for both crates). License inventory resolved
  from every package's manifest in the cargo registry cache:
  MIT 32; MIT OR Apache-2.0 98; MIT/Apache-2.0 spelling variants 8;
  Apache-2.0 OR MIT 4; Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR
  MIT 3 (Rust standard-library adjacency: `wasi`, `wasip2`,
  `wit-bindgen`); Unlicense OR MIT 3; BSD-2-Clause OR Apache-2.0 OR MIT
  2 (`zerocopy`); BSL-1.0 1 (`xxhash-rust`); MPL-2.0+ 3 (`im`,
  `bitmaps`, `sized-chunks` — pulled by `loro-internal` via `im`);
  (MIT OR Apache-2.0) AND Unicode-3.0 1 (`unicode-ident`);
  MIT OR Apache-2.0 OR LGPL-2.1-or-later 1 (`r-efi`, permissive at
  choice).
- No GPL/AGPL/SSPL or other strong-copyleft dependency exists anywhere
  in the closure; every license is permissive at choice. The MPL-2.0+
  trio is file-level weak copyleft (does not extend to linking
  consumers) but sits outside a literal "MIT/Apache only" allowlist, so
  it needs an explicit integrator disposition in `deny.toml`.
- `cargo-deny` is not installed in this environment: the `deny.toml`
  gate over these crates is recorded **unrun** (the engine workspace
  itself is unaffected: zero dependencies). The repo `deny.toml`
  `[licenses] allow = []` currently denies every license, which is only
  vacuous today because the engine has no dependencies — extending any
  license check to the seam requires an allowlist decision the
  integrator owns.

## Native-integration criteria (PD-2026-08-26-05 D2, ADR 0008 D-2)

| Criterion | Assessment |
| --- | --- |
| Usable in-process inside the engine core | Met at spike level. Loro is a pure library (`"Loro is a pure library and does not handle network protocols"`, crate doc comment); the prototype and the seam run it as an ordinary in-process dependency. |
| Bounded and low-copy on the commit-to-delivery hot path | Bounded by construction: batches are assembled from per-commit measured sizes and only the accepted range is exported, so assembly memory is bounded by the budget plus one flagged oversized single commit (asserted by the seam and spike tests under a 50_000-commit burst). Low-copy is plausible but **not measured**: no allocation-count or copy measurement ran, and it is not claimed. Two hot-path costs are recorded honestly: each `apply` performs one extra single-commit range export to measure its own wire size, and batch acceptance uses a conservative sum-of-parts estimate (observed under-fill, see evidence). SE-07 budgets both. |
| No runtime dependency on external services | Met: no network, broker, or service appears anywhere in the closure; the sync core makes no I/O calls at all. |
| Performance within the written budgets | **Open by design**: the written budgets are SE-07's deliverable and no benchmark ran (the exclusive benchmark lane and TerraBase.Benchmark gates govern; local timings are not evidence). GO is therefore not a performance verdict; PD-2026-08-26-05 D5 applies. |
| wasm32 build | Unrun: the `wasm32-unknown-unknown` target is not installed in this environment (only `x86_64-unknown-linux-gnu`). The fifth spec revision removed the wasm requirement (PD-2026-08-26-12 removed client embeddings), so this is recorded, not blocking. |

## Engineering-budget comparison (feeds SE-07; no public claim)

Hand-rolled extraction means generalizing the W4 member and streaming
state machines into the sync core. What exists and would be reused: the
streaming seam's batch contract (bounded batches, cursors, at-least-once
with stable operation identity, slow-consumer reporting — the delivery
skeleton's shape is the same), and the member plane's per-key LWW on a
bounded HLC with content-free `ConflictRecord` (the authority model).

What the client channel would additionally need, built and proven from
scratch:

1. A versioned per-scope document-state representation plus a delta
   format and codec (the actual heart: per-field diffs, container/path
   identity, dedup across commits in a batch).
2. Checkpoint/version identity and resume mathematics: what a session
   may resume from, what the server must retain, the retention floor,
   and the snapshot-resync fallback ladder.
3. Tombstone materialization, retention, and compaction semantics.
4. Idempotent re-import (at-least-once) and byte-budgeted batch packing.
5. Two SDK-side decoders (TypeScript, C#) for that format, plus
   conformance vectors — SE-04/SE-09/SE-10 scope either way, but the
   format they conform to would be ours alone.

With Loro, items 1–4 come from the base engine and are exactly what this
spike's tests exercise; what remains V2-owned is the seam (exists, tests
green), scope fan-out and persistence policy, batching/compaction
tuning, and the SDK receive path — where clients need only the
single-writer subset (import server deltas; never merge local writes),
which is materially smaller than a full CRDT client, and for which the
Loro ecosystem offers wasm (TS) and C ABI surfaces that SE-09/SE-10 must
cost explicitly.

Honest costs of the Loro path, recorded with the GO:

- 156 transitive crates enter the seam's closure (quarantined there; the
  engine core stays zero-dependency).
- License spread including MPL-2.0+ and BSL-1.0 needs the integrator's
  `deny.toml` disposition.
- The per-apply measurement export is real hot-path work SE-07 must
  budget (it can be made lazy or sampled; correctness does not depend on
  it, only the tightness of batch packing does).
- The estimate-based batch acceptance under-fills batches in the burst
  observation (max 24_696 B of a 65_536 B budget); SE-07 can tighten
  packing with measured re-export feedback. (SE-07 resolution, recorded
  in `evidence/benchmarks/se07-sync-core-delta-receipt.md`: measured
  re-export feedback now packs to 0.743 utilization against the written
  0.70 budget, delivered batches never exceed the budget, and the
  per-apply measurement export stays the bounded-memory guarantee's
  cost.)
- Loro's snapshot of a small document set measured larger than the plain
  bodies (70_532 B vs 35_264 B for 64 small documents): initial sync by
  snapshot carries structure and history overhead. Where initial-sync
  bytes matter, a state-only export is available and SE-07 should budget
  both.

Plan B stays available: the extraction budget above is the fallback cost
estimate, and ADR-0008 D-2's escalation gate is unchanged.

## Conditions on the GO

1. Integrator actions (see also the ticket's collision list): add
   `crates/terrabase-sync-core` to the engine workspace `members` list
   when wiring it in (its `[workspace]` table is removed at that
   moment), decide the `deny.toml` license allowlist covering the
   closure above, and run `cargo deny` once the tool is available.
2. SE-07 writes and measures the sync-path budgets (bytes-on-wire per
   delivered change, per-frame envelope overhead, trigger-notification
   size, commit-to-delivery latency, delivery throughput, allocations
   per frame) against the SE-04 golden vectors; this record's numbers
   are the seed inputs, not budgets.
3. SE-09/SE-10 cost the SDK receive path explicitly (TS wasm Loro vs a
   minimal single-writer decoder; C# binding vs the same) before
   committing SDK architecture.
4. Any future relaxation of the single-writer rule (client-side writes,
   optimistic patches beyond the accepted v1/v2 difference) reopens this
   record: Loro container semantics would then become a conflict-policy
   decision the charter lists as intentionally unresolved.

## Non-claims

No `qualified`, `production-ready`, `v2-faster`, or throughput/latency
statement is made anywhere in this record. Resident-memory observations
are coarse process-level samples with their limits stated in the
evidence file. The engine workspace, the 23 W4 replication contract
tests, and the streaming suite are untouched (checked green; see
evidence).
