# Benchmark evidence

The active [TS-500 C# baseline](csharp-baseline/ts500-20261004-inserts/summary.md) lives under `csharp-baseline/`, with
JSON receipts, raw samples, environment metadata and Markdown tables. Run
`nice -n 19 python3 https-document/acceptance.py --output bench/csharp-baseline/NEW_RUN_ID`
from the repository root. Every run pins a real published TerraBase release by
version and SHA-256, uses a fresh local TLS server, and measures the same five
workloads at concurrency 1, 4 and 16. CI runs a shorter correctness-only smoke
and retains its evidence as an artifact. See [the driver contract](../https-document/README.md).

The current baseline covers 10,500 logical operations and 6,510 write readbacks.
All write cases use conditional creates with document IDs unique across the
entire run, keeping the workload consistently insert-only across cases.
JSON records `write_mode: "insert_only"`. Every write is read back
after its case, including both transaction documents and every write in the
90% read / 10% write mix. Readbacks are excluded from latency and throughput
timing. The [all-writes TS-500 baseline](csharp-baseline/ts500-20261004-all-writes/summary.md)
is historical: every write was checked, but its insert/update mix depended on
case order and concurrency. The [original TS-500 baseline](csharp-baseline/ts500-20261004/summary.md)
is retained as historical evidence: it overwrote per-worker IDs and verified
only their final values, not every write. Its timings describe that earlier
workload and verification boundary.

Historical Rust qualification is retired from CI. The preserved Rust matrices
and receipts below remain historical evidence and do not establish C# product
acceptance. SQLite/Turso comparison subjects remain available.

## Historical Rust evidence

The [v2 acceptance matrix](terrabase-rust-acceptance-v2.md) defines the required
cases, the 0.40x SQLite throughput floor, and current status. The
[v1 matrix](terrabase-rust-acceptance-v1.md) is kept unchanged as history. Publish human-readable benchmark comparisons here as
Markdown tables.

Use `fixtures/<fixture-id>/` for a generator/seed/dataset manifest. Place new
matrix-qualified outputs under `<matrix-version>/<case-id>/<trial-id>/`,
including the raw JSONL samples, JSON run receipt, environment manifest, and
any profiles.
Use an immutable run ID so a rerun cannot overwrite evidence. A result table
links every trial receipt and states both source revisions, binary and fixture
hashes, environment, durability and cache mode, errors, metric distribution,
confidence interval, and scoreboard status. Preserve failed trials and negative
results. Historical and optional Firecracker development runs keep their
environment receipts in `firecracker-harness/<run-id>/`.

The `terra-rust` driver has SQLite and an initial embedded TerraBase caller.
Do not mark a comparison row `MEETS_FLOOR` until their semantics and paired trials
meet the matrix's registered resource and environment rules. The active [C# HTTPS driver](../https-document/README.md) supersedes the old
process point-read diagnostic. Historical point-read receipts remain unchanged.
Current CI runs only the real C# release smoke, without KVM, and never fills a
historical Rust matrix row.
