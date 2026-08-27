# SE-15 Benchmark Receipt: TerraBaseV2 lane commissioning

PostgreSQL-parity-or-better: inconclusive

verdict: inconclusive

No performance claim is made in this receipt. The deterministic counters
below are exact facts about the operation streams that produced them; the
wall-clock samples are engineering evidence only, single runs with no
comparand, and they support no verdict. The objective question cannot be
answered until both reference baselines run — see
`se15-postgresql-reference-baseline-receipt.md` and
`se15-v1-reference-baseline-receipt.md`.

## Run identity

- Base source: `5f8a04b` (develop, pre-packet); changed source: this
  packet's commits on branch `fleet/se15-2`.
- Adapter: `src/benchmarks/` (driver, gates, lane, record, reference,
  vocabulary), driven by the named test
  `benchmarks::tests::v2_lane_counter_characterization`.
- Launcher command (repository root, offline, locked):

  ```
  cargo test --offline --locked --lib \
    benchmarks::tests::v2_lane_counter_characterization -- --ignored --nocapture
  ```

- Profile: dev (`--lib`, no `--release`). Recorded honestly: a `--release`
  run of the same characterization was started and did not finish inside
  its 900-second window — the release profile's `codegen-units = 1` plus
  `lto = "thin"` makes the engine crate's release build slower than that
  window on this host — so the recorded values below are dev-profile values
  and are labelled as such wherever they appear. No release-mode value is
  claimed anywhere in this receipt.
- Environment: the shared build host, otherwise loaded by sibling builder
  worktrees during the run. The wall-clock rows are single-run samples
  under that load and carry no statistical weight; they are recorded
  because the discipline retains runs that flatter nothing.
- Lane configuration: seed 256 documents, warm-up 128 operations per
  workload, 3 measured rounds of 512 operations, concurrency 1, noise 5%.
  **This is a trial configuration, not a recorded baseline**:
  `LaneConfig::validate_for_record` fails for it on all three of V1's
  minimums (seed documents, operations per sample, samples), which is the
  honest status of a commissioning run.
- Correctness gates in force at the changed source: the full workspace
  test suite, dev and release profiles, plus `scripts/check.sh` (receipts
  below).
- Durability: unchanged and unweakened — every measured write commits at
  `LocalDurability::Synced`, fsync-per-commit journal records, digest
  framing, and every fail-closed envelope discipline exactly as landed.

## Deterministic counters (exact, reproducible)

Document shape: V1's benchmark document — a short name, a small integer
group, and a 256-byte payload.

| Workload | Warm-up window (128 ops) | Measured window (512 ops) |
| --- | --- | --- |
| `read` | 0 durable bytes appended, 128 documents returned | 0 durable bytes appended, 512 documents returned |
| `write` | 16,002,108 durable bytes appended, 0 documents returned | 154,246,326 durable bytes appended, 0 documents returned |
| `query` | 0 durable bytes appended, 128 documents returned | 0 durable bytes appended, 512 documents returned |
| `mixed` | 4,195,695 durable bytes appended, 89 documents returned | 24,475,311 durable bytes appended, 359 documents returned |

Seeding 256 documents appends 12,947,121 durable bytes, reproducibly: the
test `the_durable_byte_counter_is_reproducible` pins that seeding the same
count through two fresh engines writes byte-identical totals, which is what
makes these counters lane facts rather than timing samples.

Contracts the counters pin:

- **Reads and queries append nothing.** Both windows record exactly `0`
  durable bytes for `read` and `query`, pinned by
  `the_v2_lane_measures_deterministic_counters_per_workload`.
- **Every read and every seeded-query operation returns its document** —
  512 of 512 in both windows, so the mapped equality query does match the
  seeded set.
- **The mixed workload's shape checks out against its own ratio**: 512
  operations at 50/30/20 yield 359 documents returned (the read and query
  shares), and 128 warm-up operations yield 89 — both consistent with the
  5/3/2 per-ten selection pinned by `the_mixed_workload_selects_v1_ratio_by_index`.

## Lane observation, recorded with no verdict

The write-bearing windows append far more durable bytes than the document
payload they carry: the `write` measured window appends 154,246,326 bytes
for 512 writes of a ~300-byte document, and the `mixed` window appends
24,475,311 bytes for roughly 150 writes plus 100 queries. Seeding alone
costs ~50 KB of durable bytes per seeded document.

This is an exact, reproducible counter, and it is recorded because it is
the kind of fact the lane exists to surface. It is **not** a verdict: there
is no reference baseline on either side to compare it against, no budget in
force for it, and nothing here says whether PostgreSQL or TerraBase V1
would do better or worse on the same workload. Naming a follow-up is the
integrator's call, not this receipt's.

## Wall-clock commissioning samples (engineering evidence only)

Throughput in operations per second, one sample per measured round,
dev profile, concurrency 1, shared and otherwise-loaded host:

| Workload | Sample 1 | Sample 2 | Sample 3 |
| --- | ---: | ---: | ---: |
| `read` | 22,583 | 23,232 | 23,130 |
| `write` | 11 | 10 | 5 |
| `query` | 1,445 | 1,456 | 1,488 |
| `mixed` | 17 | 18 | 18 |

These numbers support **no** verdict and no objective status. They are
single runs, in the dev profile, under host load, with no comparand, no
reference baseline, and no gate applied. They exist to prove that the
commissioning sampler drives the whole gate machinery end to end on real
engine work — and the sampler's own outcome is forced to `inconclusive` by
construction, pinned by
`the_commissioning_sampler_reports_inconclusive_without_references`.

A gate-consistency check was run on them: the `read` workload commissioned
twice through two fresh engines and compared through the gate reads
`Stable` (pinned by `a_self_comparison_reads_as_parity`), which is the
expected reading of a self-comparison and a sanity check on the gate math
rather than a measurement of the engine.

## What is owed next

- The PostgreSQL reference side: the operator provides the pinned endpoint
  and captures its `SELECT version()` output.
- The TerraBase V1 reference side: the operator runs the pinned V1 harness.
- The `session` workload's adapter slot: the TBEP UDS host session needs the
  C# host process beside the engine; the slot is declared in
  `Workload::driver_status` and names its owner.
- The sync-path workload class's four adapter slots
  (`sync-commit-to-delivery-latency`, `sync-delivery-throughput`,
  `sync-bytes-on-wire`, `sync-trigger-notification-size` —
  PD-2026-08-26-05 D4, PD-2026-08-26-13 D1): each is declared in
  `Workload::driver_status` with its owner, mapped in the workload-mapping
  document's sync-path class section, and recorded unrun-with-reason in
  `se15-sync-path-workload-slots-receipt.md`.
- A recorded run (not a commissioning trial) once the references exist,
  at V1's minimums: 1,000 seed documents, 10,000 operations per sample,
  5 samples.

## Validity notes

- Every wall-clock number above is dev-profile. No release-mode number is
  claimed; the attempted release run is recorded above as unfinished.
- The counters are independent of profile and load, which is why they —
  and only they — are stated as facts.
- The objective status is `inconclusive`: the honest outcome for the
  objective question while both reference baselines are unrun, and the only
  outcome the adapter's fail-closed rule can produce from this state.
