# Execution brief: TerraBase-only family coverage

The following are the allowed local TerraBase commands after the frozen
release member and bench driver are handed off to bench_owner. All run at
`nice -n 19`, use the member's cgroup evidence, and write into a fresh run
directory.

```text
# point operations (all four workloads)
nice -n 19 bench/target/release/terrabase-bench-lane --target terrabase --workload read --seed 1000 --ops 10000 --samples 5 --concurrency 16 --warmup 1000 --out <RUN>
# repeat with --workload write, query, mixed

# bulk ingest, single-node only
nice -n 19 bench/target/release/terrabase-bench-lane --ingest-target terrabase --ingest-topology single-node --documents-per-sample 100000 --batch-size 1000 --ingest-samples 5 --warmup-batches 1 --out <RUN>

# local TerraBase auto-index diagnostic (no comparator claim)
nice -n 19 bench/target/release/terrabase-bench-lane --family auto-index --target terrabase --documents 1000000 --samples 100 --writes 1000 --out <RUN>

# local sync lane; use sync.sh so its dedicated binary and envelope checks run
nice -n 19 bench/scripts/sync.sh
```

`indexed-query.sh` cannot produce a TerraBase-only success: its validator
requires all three side images and all three complete side documents
(`bench/src/indexed_query.rs:485-589`), and the shell invokes each configured
side (`bench/scripts/indexed-query.sh:67-93`). A missing PostgreSQL/RavenDB
adapter must stay unavailable. Likewise served `auto-index.sh` requires both
TerraBase and RavenDB endpoints plus comparator, shadow, logical-cost, and
write-budget evidence (`bench/scripts/auto-index.sh:18-29,83-85`). Use the
local auto-index command above for TerraBase-only coverage.

RF2 is not locally equivalent to `local-member.sh`: that tool starts one
member and emits one member cgroup receipt (`bench/scripts/local-member.sh`),
while RF2 requires two real members and separate topology evidence. Do not
label a single local member as RF2 or combine its receipt with single-node
numbers. Sync CDN and cross-region lanes similarly require real external
endpoints and remain unavailable when absent.
