# Measurement contract audit: current TerraBase write lane

Audited frozen source `/tmp/terrabase-qual-fae32/bench/src/lane.rs` and its
`config.rs`/HTTP driver.

`warmup_operations=1000` is the total warmup window for one workload run, not
per worker and not per measured sample. `lane::run` divides it across 16 slots
(`per_slot_warmup` plus remainder), waits at `warmup_end`, and excludes it from
all five samples. Each sample then performs `operations=10000` total across
the slots, so the run performs 50,000 measured operations and 1,000 warmups.
The executor's barriers start and stop each sample together; output is emitted
only after all workers join and the five paired windows are reduced.

For the write workload, setup seeds 1,000 documents, then warmup writes use
global indexes 0..999 and measured writes use `sample * 10000 + operation_offset`
for global indexes 0..49,999. `document::measured_id(index)` maps these to
unique `measured-{index}` identifiers. Thus a successful write run should
attempt 52,000 document writes and finish with roughly 51,000 live documents
(subject to any pre-existing data policy); it is not expected to reach that
count after only 8,881 facts.

The physical database observation of 9,880 documents / 8,881 facts after 74s
is consistent with a live, partially completed run and is not evidence of a
deadlock. At an observed ~120 acknowledged writes/s, 51,000 measured plus
1,000 warmup operations imply roughly 425s of operation time, before seed and
setup overhead. Do not lower the workload or declare success from partial
database counts. Let the bounded timeout and final receipt decide completion.

The benchmark owner should retain the complete lane log, final result JSON,
source manifest, executable digest, and database/resource evidence. A failure
before all five samples is failed/raw and cannot supply partial timing rows.
