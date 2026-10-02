# Pinned local SQL engine CRUD driver

This standalone binary compares **local Rust SQL binding calls** on one
deterministic, single-client key/value CRUD trace. Timings include binding,
row conversion, and engine work; they cannot isolate storage-core speed.
It does not measure the
TerraBase HTTPS API, document semantics, replication, S3, object-durable ACK,
concurrent transactions, or regional recovery. The existing
[HTTPS diagnostic](../bench/https-document-point-read/https-doc-20260926-01/summary.md)
and [P04 deleted-history diagnostic](../bench/p04-three-way-protocol-20260925.md)
remain separate.

The two lanes use the same binary, schema, SQL text, generated keys and values,
and seven prepared operations per key: insert, read hit, update, read hit,
delete, read miss, and upsert. The upsert leaves a final row. Every write is
one explicit `BEGIN IMMEDIATE`/prepared statement/`COMMIT` operation; every
read materializes the full payload and revision and finishes the statement.
The timestamp starts after the cached prepare and stops after COMMIT or
completed read cleanup. Value
generation, correctness comparison, JSONL output, final scan, integrity
check, and reopen are outside each latency sample. The receipt separately
records the wall duration of the seven-step cycle loop, including value
generation, validation, and JSONL writing, but excluding setup, final scan,
integrity check, and reopen. It is not isolated engine service time.

Both lanes request a 4,096-byte page, WAL journal, `synchronous=FULL`,
and a 64-MiB page cache. The driver **reads back and requires** those
values before creating the identical table. SQLite also sets and reads
back `wal_autocheckpoint=1000`. Pinned Turso does not support that PRAGMA;
its WAL source fixes a 1,000-unbackfilled-frame threshold. Turso triggers
an automatic passive checkpoint after a commit when its frame count is
**greater than** that threshold and may checkpoint on close. SQLite's
configured WAL autocheckpoint is a separate engine policy. The numeric
thresholds do not establish identical checkpoint timing or cost.
SQLite uses existing repository dependency `rusqlite 0.40.2` with bundled
SQLite. Turso uses the Rust binding `0.8.1` from the sibling TerraBase
checkout's `crates/turso` submodule (release v0.8.1, gitlink
`8549c16595d2faf1bdd6ee24aee0be8bfabb3d4a`), with default features off,
exactly as TerraBase compiles it: the gitlink plus TerraBase's ordered
`patches/turso` series, applied by TerraBase's `scripts/ci/prepare-turso.sh`.
The SQLite library version and Turso's reported SQLite compatibility version
are recorded separately; they are not the same engine or release version.
The build script hashes the whole Turso checkout, including files the patch
series adds, through a fresh scratch Git index. Only the pinned tree's own
`.gitattributes` apply; global, system, and `info/attributes` settings are
ignored, and a configured filter driver is refused. It rejects any tree other than
`4da45c6e8290634cff70f371d123b0564206e4b0` (the tree `prepare-turso.sh --tree`
prints for that TerraBase revision). It embeds that tree, the SHA-256 of the
patch diff, and the SHA-256 of each patched file in every receipt. Run
`bash ../terrabase/scripts/ci/prepare-turso.sh` before building; a TerraBase
revision that changes the patch series needs a matching pin update here.

The [registered host diagnostic and completed scorecard](../bench/local-engine-crud-v1.md)
fix the input bounds, alternating run order, source checks, and acceptance
rules. That completed run used the earlier `0.8.0-pre.13` binding at gitlink
`64b8ef5742fc18937f9c89806c81e3f6475dc7a3`; its receipts stay as recorded.
This is separate from the v1 guest qualification matrix. The crate
pins the repository's Rust 1.98.1 toolchain; older toolchains have not been
verified. Its **private** `Cargo.lock` was generated under a lock-only
resource lease. A compile check found and prompted a correction to an
ambiguous integer type; no successful run or speed result existed when this
host protocol was registered. Use the Make targets; refresh the lock only
when needed:

```sh
make -C ts-benchmark local-engine-fmt local-engine-check local-engine-lint
make -C ts-benchmark local-engine-build
```

Each host lane requires a fresh database path, a fresh output directory,
an observed host environment manifest, and source revisions. Use the same
CPU affinity for every lane; generate the manifest from inside that affinity
with `host-manifest.py`. The driver requires its actual `/proc/self/status`
CPU affinity and canonical database parent to match the manifest. This
example assumes the
selected CPU is allowed by the host and the per-lane directory exists:

```sh
nice -n 19 ionice -c 3 taskset -c "$CPU" \
  python3 ts-benchmark/local-engine/host-manifest.py \
  --output "$LANE/environment.json" --database-parent "$LANE"
nice -n 19 ionice -c 3 taskset -c "$CPU" \
  ts-benchmark/local-engine/target/release/local-engine-scorecard \
  --engine sqlite --cycles 1000 --body-bytes 256 --seed 1 --trial 1 \
  --database "$LANE/data.db" --output "$LANE/results" \
  --environment-manifest "$LANE/environment.json" \
  --benchmark-revision <exact-ts-benchmark-commit> \
  --terrabase-revision <exact-terrabase-commit> \
  --turso-revision 8549c16595d2faf1bdd6ee24aee0be8bfabb3d4a
```

Use `--engine turso` for the other lane, with a different fresh directory.
The run record must independently capture the compiled binary SHA-256,
source checkout and submodule patch inventory, build flags, CPU affinity,
host/kernel/filesystem/memory/swap and observed load. The driver records
its binary, source/manifest/lock hashes, and environment manifest hash.
A matching driver receipt alone does not prove that the source was compiled
from the claimed commit; compare it with the frozen build inventory. Host
process timings can be affected by other work and do not qualify the v1
guest hardware matrix.

The completed [four-pair host run](../bench/local-engine-crud-host-20260926T2100Z/summary.md)
retains the raw samples, receipts, source snapshot, and post-reopen database
images. The results describe this host and workload only.
