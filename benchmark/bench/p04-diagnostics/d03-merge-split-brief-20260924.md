# P04 D03 merge-split diagnostic brief

Status: **source-only proposal; no D03 derivative, build, or VM run exists**.
This brief is for choosing a bounded next diagnostic. It does not authorize an
optimization or change the sealed a2 and D02 packets.

## Evidence and question

The sealed D02 run is
`d02-a2-gates-lintfix-20260924T224225Z/results/p04d02a2diag20260924t2258z/`.
Its summary SHA-256 is
`0422ede7d0d6aeb9716a8ae58f2eff9e79d1d9f8830478680d9344a40b6bcf56`,
receipt `e2f70094e730bdbeeb6c8064020de1dce84d727cf50bf54b9068fbdb46dde99a`,
and samples `4e672b8bdeb8c406310454885e9fb50f9dc602a4540dfb2ea0f1842b9dfc9f25`.
The measured ELF is the D02 instrumented derivative `08876c79991489c3727afb12e07802927d839f0dde341160a2c1ce16143067d4`,
bound to source manifest `e6a239c190286259c0aecd855e23b0570b1ce9bda833b26dc9583f4d5476b4a6`,
81-file inventory `deeffbbbc7b1559de4b47c286a904bee64967f20a0d012f66bd8c3da2b9303f7`,
and protocol `468f86c2a8989ea2c054203f997ce546feb107ab2ac2f443ff6f629491840cc1`.

All 33 ordered scans were successful and empty. For the 32 warm slots, the
upper-median `Storage::scan_d02` wall bracket was 1,277,264 ns and the broad
merge bracket was 1,015,590 ns. The median per-scan merge share was 80.9%.
Each scan examined 2,048 run records and read 60,436 run-block bytes, with
one descriptor, no mutable keys, and no block evictions or rereads. This
identifies the broad merge path as the observed cost center in one instrumented
VM; it does **not** show whether key/version selection or the repeated cursor
advances inside that bracket dominate. The whole-operation thread CPU clock
cannot be divided by D02's `Instant` wall phases. Block-load time is nested,
not additive; D02's 64 empty brackets (30/40/235 ns min/median/max) were not
subtracted.

Source basis: `tb-storage/src/local.rs` places `merge_started` before mutable
iterator setup and the `while rows.len() < row_limit` loop. That loop chooses
the next key, resolves mutable and run versions, advances each matching run
cursor, and possibly emits a row. `advance_run_cursor` calls `ScanCursor::advance`
in `tb-storage/src/run.rs`; that method may parse records and load checked
blocks. The current `merge_ns` includes all of this work. The D02 storage
files are SHA-256 `f9f8846b2bdcb54a2fbcf5898756569e64af807affa9ea3bbfcab54bd914998e`
(`local.rs`) and `61730588c96517dbce4f5105e1cc8c8271e25dfc1cf40f38480e4fe08b9dcfba`
(`run.rs`).

## Minimal prospective derivative and protocol delta

If chosen, copy the frozen D02 **81-file** packet to a new read-only D03 source
ID with its own inventory, delta, lock binding, protocol, build logs, and ELF.
Also copy the byte-identical sealed-a2 acceptance matrix at
`ts-benchmark/bench/terrabase-rust-acceptance-v1.md` (SHA-256
`03188ba52673293a3f32617034079d71056c2387bc7153e78391262bf063725d`)
so the supporting baseline test target has its complete `include_bytes!`
closure; inventory that file separately from the D02 executable's original
81-file identity.
Keep the same 1,024-row/body-1/seed-1 all-deleted checkpoint fixture, one
first-after-fixture plus 32 ordered warm scans, the 64 D02 calibration samples,
all raw slots/failures, checksums, work counters, and one-run state checks.
Do not run SQLite, add a ratio or CI, mix D03 timings with D02/a2 timings, or
modify a2/D02 files. Store any future results as Markdown tables under
`ts-benchmark/bench/`.

Limit source edits to an optional D03 probe in copied `tb-storage/src/local.rs`,
its export in copied `lib.rs`, and a distinct D03 driver binary plus Cargo/Make
registration. Reuse `run.rs`'s existing nested block-load timer; no run-format
or normal-scan behavior change is needed. The probe should record, for each
scan, these raw values and counts:

| D03 field | Exact boundary | Decision it supports |
| --- | --- | --- |
| `merge_selection_resolution_ns` | Sum of disjoint wall brackets around next-key choice, version/winner resolution, value materialization, and output decision, excluding each later cursor advance | Whether winner selection and materialization deserve a targeted allocation/algorithm investigation |
| `merge_later_advance_ns` and `merge_advance_calls` | Sum of wall brackets immediately around `advance_run_cursor` calls **inside** the merge loop only; initial advance stays in its D02 phase | Whether record parsing and cursor progression, rather than winner selection, deserve the next probe |
| `merge_residual_ns` | Checked difference between inclusive `merge_ns` and the two disjoint sums; preserve an invalid-overlap flag if subtraction fails | Exposes unbracketed setup/bookkeeping or a bad probe boundary; do not silently attribute it to selection |
| `block_load_initial_ns`, `block_load_later_ns` | Snapshot the existing per-cursor nested block-load total after initial advances and after merge, then take checked deltas | Distinguishes read/decode nested in initial advance from any load nested in later advances; never add to exclusive phases |
| `resolved_keys`, `tombstone_winners`, `put_winners`, `no_visible_version` | Count once per distinct key after snapshot winner resolution, before an output row is admitted | Establishes that the 2,048 examined records represent the expected 1,024 current-snapshot tombstone winners rather than an accidental empty result |

Keep these fields missing/null on unrun or failed slots, and preserve the
error on a failed slot. Require all disjoint phase sums to fit inside the
inclusive scan/merge walls or mark the sample invalid. Counts must be
snapshot-aware: a tombstone **winner** is the highest visible version after
considering mutable and all run candidates, not merely a tombstone record
encountered by a cursor.

Per-advance `Instant` probes can themselves cost a meaningful fraction of a
roughly 1-ms scan. Record every probe call count. Outside the measured scans,
run 32 batches of empty timing pairs before and 32 after the scan series,
each matching the observed number of selection and advance brackets; report
per-batch min/median/max total overhead and drift. Do **not** subtract that
overhead from phase values or call the corrected difference CPU time. If the
calibration scale overlaps the observed subphase difference, the split is
inconclusive and the next decision is a lower-overhead probe design, not an
engine optimization. The one-VM result remains descriptive.

## Safety gates before any engine optimization

The D03 fixture should assert current-snapshot sequence 16, one immutable
run, zero mutable bytes, 1,024 resolved keys, 1,024 tombstone winners, zero
put winners and output rows, and the established D01 fixture digest. Tests
for any later optimization must also cover an older valid snapshot before the
deletes, a mutable put that resurrects a key, a mutable tombstone that hides
one, checkpoint-before-deletes/multiple-run shadowing, sparse-tail output,
equal-sequence conflicts, corruption of a run block or directory, and
admission/eviction paths. Skipping records or blocks must never hide a valid
older put, expose a tombstoned value, or bypass a corruption error. Re-run
the real caller and the registered paired acceptance protocol separately
after any accepted engine change; D03 timing is not an a2 speed verdict.

One no-semantic-change opportunity is already visible in source: the merge
loop clones the entire `ScanCandidate`, including its boxed key, although it
uses only the `CommitSeq` and `SegmentValueRange` fields before advancing the
cursor; both fields are `Copy`. A separately reviewed refactor could copy
those fields instead and leave winner order, bytes, errors, and scan-work
counters unchanged. This does **not** require D03 to identify a redundant
clone, but D02 does not measure allocation cost or establish a material
latency benefit. Do not describe that refactor as an optimization win until
the safety cases above and independent uninstrumented timing pass.
