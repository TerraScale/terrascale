# SE-07 Benchmark Receipt: TBEP overhead characterization (budget items
E-13..E-15)

No performance claim beyond the measured, deterministic byte and
write-call counts recorded here. Objective status against
PD-2026-08-26-03: **inconclusive** for any wall-clock statement — the
reference baselines and statistical gates are SE-15's deliverable.

## Run identity

- Base source: `33dd166` (pre-change); changed source: this packet's
  commit on branch `fleet/se07-2`.
- Launcher commands (repository root, offline, locked):
  - `cargo test --offline --locked --release -p terrabase-tbep --test overhead_characterization -- --nocapture`
    (red run: same command at base `33dd166`).
  - `cargo test --offline --locked --release -p terrabase-tbep --test uds_characterization -- --ignored --nocapture`
    (E-15, the existing SE-14 harness re-run).
  - Dev-profile run added by the warden repair (the packet's receipts had
    run `--release` only):
    `cargo test --offline --locked -p terrabase-tbep` — green across the
    crate's 7 suites, `overhead_characterization` 2/2; no defect was found
    in this crate's dev-profile behavior (the off-by-one debug assertion
    the same loop caught in sync-core had no counterpart here).
- Profile: `--release`. Environment of the E-15 run: Linux x86-64, 28
  CPUs, otherwise-unloaded builder; one connection, synchronous
  request/answer, 200 discarded warm-up rounds (the SE-14 method,
  unchanged).

## Measurements (red = pre-change, green = post-change)

| Item | Red (base `33dd166`) | Green (this packet) | Verdict |
| --- | --- | --- | --- |
| E-13 registry closure (code sweep of every `u16`) | (not previously measured) | every code either decodes to exactly one registered kind (46 kinds, each round-tripping its own code) or fails closed | guard (deterministic) |
| E-13 per-class envelope overhead, maximal identities | control 37 B, operation 39 B, subscription 39 B, trigger 39 B, delivery 39 B; plain-envelope minimal frame 23 B; saturated identities on a u16 kind code reach exactly `FIXED_ENVELOPE_BYTES` = 35 B (fixint kind codes ride 2 B lower at 33 B); all within 35 + 4 B on the stream | unchanged (the envelope structure is untouched) | `no-improvement` (definition-check; the bound is now measured per class by a named test) |
| E-14 stream-write path (536-byte body frame) | 2 write calls, 9 allocations (prefix buffer + envelope buffer path) | 1 write call, 6 allocations; byte-identical wire output asserted in-test | `v2-faster` (deterministic write-call and allocation counts) |

## E-15 UDS timing characterization: engineering evidence only,
verdict `inconclusive`

The SE-14 same-host UDS round-trip harness re-ran at the changed source
(numbers in microseconds, same method and shape as the SE-14 record):

| Row | SE-14 record (base) | This run (changed) |
| --- | ---: | ---: |
| `supervision.health` (10,000 rounds): mean / p50 / p99 / max | 82 / 69 / 199 / 5,076 | 29 / 24 / 54 / 3,096 |
| `sync.mutation_submit` (2,000 rounds): mean / p50 / p99 / max | 732 / 614 / 2,404 / 26,091 | 78 / 61 / 117 / 1,684 |

These wall-clock values support **no** verdict: single runs, no
statistical gates, no TerraBase V1 or PostgreSQL reference, different
run conditions (host load and state) — the written budget E-15 records
them as retained engineering evidence with the explicit verdict
`inconclusive`. They are not comparable to a Benchmark Receipt, and the
raw difference between the two rows must not be read as an improvement
claim. The deterministic counterpart of this receipt (E-13/E-14) is the
part that carries verdicts.

## Invalid / failed runs retained

- The first green attempt of `stream_write_uses_one_buffer_and_one_write_call`
  asserted "at most two intermediate buffers" and measured 6 allocations
  (the assertion failed). Investigation showed the count is dominated by
  the codec's body-encode buffer growth, not the framing path; the
  budget's measurable form is the write-call count (exactly 1) plus a
  same-frame, in-test comparison against the two-write path it replaces
  (6 < 9 allocations, byte-identical output). The budget document's E-14
  wording was corrected to that measurable form before the recorded
  green run; the failed assertion run is retained here as the reason.
- The E-15 timing run is retained although it cannot support a verdict
  (reasons above).

## Objective status (canonical line, added by SE-15)

PostgreSQL-parity-or-better: inconclusive

Added by the SE-15 standing objective-status discipline
(PD-2026-08-26-03 D3, TerraScale/TerraBaseV2#23) so this receipt carries
the machine-checkable status line `scripts/check_benchmark_receipts.py`
fails closed on. No measured value, verdict, or note above is altered: the
SE-07 runs measured deterministic counters only, so the objective question
is unanswerable from them until the reference baselines exist and run.
