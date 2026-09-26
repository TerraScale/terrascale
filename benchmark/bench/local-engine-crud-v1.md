# Raw local SQL engine CRUD scorecard v1

**Status: four host-process pairs completed and verified. This is a diagnostic,
not v1 hardware qualification.** This scorecard measures
the embedded Turso and SQLite SQL engines through their local Rust bindings
in a single binary. Binding and row-conversion costs are part of each sample;
the scorecard cannot isolate storage-core speed.
It excludes TerraBase's document API, HTTPS/TLS path, replication, S3,
crash recovery, and object durability. The existing
[HTTPS point-read result](https-document-point-read/https-doc-20260926-01/summary.md)
cannot be placed in this table as a SQLite-comparable latency.

## Registered workload and boundaries

| Dimension | Required value |
| --- | --- |
| Driver | [`local-engine-scorecard`](../local-engine/README.md), one feature-independent release binary for both lanes |
| Toolchain | Repository-pinned Rust 1.98.1 with the crate's private `Cargo.lock`; older toolchains are unverified |
| Engine sources | Turso binding `0.8.0-pre.13` at TerraBase submodule gitlink `64b8ef5742fc18937f9c89806c81e3f6475dc7a3` plus recorded tracked diff and file inventory; `rusqlite 0.40.2` with bundled SQLite; record actual SQL compatibility versions |
| Host | Sequential processes on the same host and filesystem, each pinned to the same allowed CPU at nice 19/idle IO, with a fresh database/output path and an observed per-lane host manifest; record CPU model, kernel, memory/swap, filesystem, load, and cache policy |
| Build | One `--release --locked -j 2` binary built with Rust 1.98.1 through the low-priority Make target; record exact source/diff/lock/toolchain/binary hashes and build command |
| Inputs | 1,000 cycles, 256-byte values, seed 1, unique deterministic 32-bit-permuted integer keys; seven SQL operations per cycle; one client |
| Schema | `items(id INTEGER PRIMARY KEY, payload BLOB NOT NULL, rev INTEGER NOT NULL)` in both lanes |
| Trace | Insert v0 → point read v0 → update v1 → point read v1 → delete → point miss → upsert v2; every value and changed-row count checked |
| Durability | Both lanes must **read back** 4,096-byte pages, WAL, `synchronous=FULL`, and 64-MiB cache; SQLite also sets and reads back `wal_autocheckpoint=1000`, while pinned Turso uses a source-level 1,000-unbackfilled-frame threshold without a supported `wal_autocheckpoint` PRAGMA; every write is a separate `BEGIN IMMEDIATE`/prepared execution/`COMMIT` |
| Timed interval | After cached prepare: write includes begin, SQL execution and commit; read includes query, full payload/revision materialization, statement completion and cleanup. Generation, validation, JSONL emission, reopen, final scan, and integrity check are outside each sample |
| Warmup | None: every lane starts with an empty database, and early engine cache behavior remains in the 1,000 recorded cycles; do not evict the host OS page cache between lanes |
| Verification | Every point result and write count, exact deterministic final rows after reopen, `PRAGMA integrity_check = ok`, matching trace SHA-256 and final-state SHA-256, and 7,000 raw samples per successful lane |
| Bounds | Driver rejects cycles outside 1–10,000 and value sizes outside 1–4,096 bytes; unique output/database paths and manifest are mandatory |

`BEGIN IMMEDIATE` is a common single-client transaction boundary here.
It does **not** prove equal lock acquisition or concurrency semantics between
the engines. WAL/FULL is the requested and read-back local durability mode;
this workload does not inject power loss or claim equal crash guarantees.
Automatic checkpoint triggers and close behavior differ by engine, so the
same threshold number does not prove equal checkpoint timing or cost.
The engines also report different SQLite compatibility versions. A lane
with a different effective setting, changed trace/final hash, failed reopen,
or missing source/build identity is a failure, not a timing observation.

Run four complete fresh-path host pairs sequentially in this preassigned order:

| Pair | First lane | Second lane |
| --- | --- | --- |
| 01 | SQLite | Turso |
| 02 | Turso | SQLite |
| 03 | SQLite | Turso |
| 04 | Turso | SQLite |

Use the [driver commands](../local-engine/README.md) with trial 1–4 and
fresh paths. Before measurement, verify the sibling TerraBase revision,
Turso gitlink, tracked-diff SHA-256 and changed-file hashes; verify the
private `local-engine/Cargo.lock` generated under a lock-only resource lease.
Hash the exact binary and use it in both lanes of every pair. Archive commands,
host manifests, binary/source/lock receipts, JSONL samples, failures, and
host scheduling/cache evidence under a new immutable `bench/` run ID. Do
not replace prior data. Aggregate only after all eight lanes pass. Keep
per-pair per-operation p50/p95/p99 and report the median of the four
per-process values; do not pool 1,000 observations across processes or convert
these four pairs into a hardware-qualified or enterprise throughput verdict.
Host scheduling, page cache, swap, and background load are uncontrolled.
A descriptive
ratio may be reported only for matching verified operations, with its
direction and limitations stated.

## Results

The immutable [run packet](local-engine-crud-host-20260926T2100Z/summary.md)
contains all eight receipts, 56,000 raw operation samples, host manifests,
commands, source snapshot, and the three-file Turso patch. Values below are
microseconds, with each engine cell the median of four lane p50/p95/p99
values. Four pairs do not support a confidence interval or statistical
ranking. SQLite recorded lower latency at all three listed quantiles for
every operation in this particular host run.

| Operation | SQLite p50/p95/p99 | Turso p50/p95/p99 | Verified pairs | Status |
| --- | --- | --- | --- | --- |
| Insert | 441.206 / 724.311 / 1676.514 | 680.242 / 929.333 / 2218.439 | 4/4 | Verified host diagnostic |
| Point read after insert | 10.502 / 12.726 / 23.250 | 13.845 / 16.000 / 29.751 | 4/4 | Verified host diagnostic |
| Update | 442.793 / 696.086 / 1493.485 | 667.576 / 928.057 / 2037.874 | 4/4 | Verified host diagnostic |
| Point read after update | 9.644 / 11.491 / 21.646 | 12.825 / 14.849 / 26.950 | 4/4 | Verified host diagnostic |
| Delete | 434.387 / 678.702 / 1761.550 | 660.525 / 886.627 / 1930.502 | 4/4 | Verified host diagnostic |
| Point miss after delete | 8.297 / 9.757 / 19.067 | 10.726 / 12.521 / 24.855 | 4/4 | Verified host diagnostic |
| Upsert | 437.080 / 687.063 / 1385.077 | 656.229 / 931.054 / 2002.273 | 4/4 | Verified host diagnostic |

This limited local Rust API diagnostic does not qualify the v1 guest matrix
and cannot establish TerraBase API throughput, storage-core superiority, or
an enterprise performance claim.
