# Turso 0.8.2 native FTS contract

Source: `tursodatabase/turso` tag `v0.8.2`, commit
`5c168be5ef6a32ac966daf5983502a4f694b37ad`. No version upgrade.

```sql
CREATE TABLE t(id INTEGER PRIMARY KEY, title TEXT);
CREATE INDEX t_fts ON t USING fts(title);
SELECT id FROM t WHERE fts_match(title, ?1);
SELECT id, fts_score(title, ?1) AS score FROM t
WHERE fts_match(title, ?1) ORDER BY score DESC LIMIT ?2;
```

Multi-column indexes use `USING fts(title, body)` and pass the same indexed
columns before the query argument to both functions. Higher BM25 scores rank
first. Upstream tests exercise bound query parameters and `ORDER BY score DESC`.
The native smoke also probes `USING fts(json_extract(doc,'$.body'))` and the
matching expression in `fts_match`/`fts_score` in a separate process. The original
amd64 native build observed CREATE INDEX, expression match and positive score
working; expressions are not known to be unsupported. Rejection remains diagnostic
and does not fail an artifact whose derived text-column FTS passed. The engine
uses ordinary derived text columns to materialize bounded candidate scores once.

The native library must compile `turso_sdk_kit` with Cargo feature `fts`
(`turso_core/fts`, backed by Tantivy). `index_method` is a runtime database
capability, not a Cargo feature: pass `index_method` in the C ABI database
configuration's `experimental_features`. The vendored .NET driver's
`TursoBindings.NewOpenedDatabase` supplies this for normal and encrypted opens.
It is always on, with no application setting. Reopened databases require it too.

The build selects `turso_sync_sdk_kit --features turso_sdk_kit/fts`, then renames
its library to `libturso_sdk_kit.so`, exactly as upstream's .NET Makefile does.
This retains the sync ABI used by existing driver methods as well as the core.

Evidence in the pinned upstream tree: `sdk-kit/Cargo.toml`,
`sdk-kit/src/rsapi.rs` (`database_opts`),
`tests/integration/index_method/mod.rs` (`test_fts_sql_queries` and bound-query
tests), and `core/index_method/fts/mod.rs`.

## Build and verification

```sh
nice -n 19 scripts/vendor/build-turso-native.sh amd64 src/vendor/Turso.Data.Sqlite/native
# On an arm64 builder, use arm64 instead of amd64 (or configure BuildKit emulation).
```

The helper makes one clean build. CI makes one build per architecture per run,
publishes `turso-native-amd64` / `turso-native-arm64`, and verifies their manifests
before Fast/Slow/image builds. Compare `SHA256SUMS-linux-*` from two independent
CI runs with the same native inputs for the reproducibility proof. No build loop
runs in CI. Only main writes the native BuildKit cache; PRs read it.

Both ELF architecture checks remain: the native library is checked before
publication, and the existing production apphost guard in `Dockerfile` is retained.
The native smoke verifies match/positive score, committed insert/update/delete,
and rollback visibility for insert/update/delete. Fast additionally verifies the
normal .NET driver, persisted-index reopen, and transaction consistency.

Static validation passed: C smoke against the pinned header, actionlint (without
its unavailable shellcheck executable), cache-policy fixtures, x64/arm64 MSBuild
asset selection, missing-native build failure, and vendor patch reverse-apply.
The original native inputs passed native/ELF/smoke on amd64 and arm64 in CI run
39946883671 at 3d9a13c (jobs 120392135692 and 120392135570). The native budget patch
and seek assertions added afterward still need compilation/runtime proof. Release/Fast
and two-run SHA equality for identical final native inputs remain unverified.
The old security audit describes the replaced NuGet payload; its documented
crossbeam-epoch risk is not claimed resolved by this change.

Needs format bump. The deploy lane owns `StorageFormat.cs`.

## Native request budgets

`scripts/vendor/turso-native-budget.patch` is applied deterministically to the pinned
0.8.2 archive before compilation. It adds the internal C ABI
`turso_connection_set_fts_progress_handler(connection, callback, error_out)` and
`SqliteConnection.SetFtsProgressHandler(Func<ulong, ulong, bool>?)`. Configure/clear
only while the connection is idle; callbacks must not reenter Turso. Arguments are
additive work and conservative byte charges; true/nonzero aborts with native
`TURSO_INTERRUPT` (5), mapped to `SQLITE_INTERRUPT` (9) by the driver. The managed
handle roots its delegate until clear/deinitialization; exceptions abort without
crossing the unmanaged boundary.

Start/StopQueryBudget registers/removes this callback for Query/Count/assert_count.
Native charges share the request's existing work/deadline; native bytes use the
plan scan-byte ceiling, capped at 128 MiB even for unlimited plans. Foreground memory
admission is reserved before native allocation and released with the query budget.
Shared segment-byte and assembled-searcher caches retain no payloads: query memory
admission ends after the uncached native reader/command has disposed its cursor.
This also prevents invalidated snapshots or earlier writes from leaving resident
segment/searcher references across sequential queries. Per-cursor state is reset on
close/abort/rollback; prepared query objects cannot retain it after cleanup.
Charges include segment residency, temporary chunk copies, identity arrays,
tombstones and scorer/result buffers. They are conservative cumulative charges rather than
measured peak RSS; repeated searches in one request can exhaust the ceiling.

The callback polls loading/assembly, identity extraction, tombstone application,
streaming posting advancement and ordered scoring. Ordered search uses a checked
LIMIT-sized heap instead of opaque TopDocs traversal. Term/document work is charged
before scorer construction. **Cancellation is cooperative:** individual Tantivy
parser, weight/scorer/advance and identity-sort calls remain opaque; checks bound
inputs and run between calls, not inside them. This is not a hard wall-clock deadline
guarantee for a single call.

The mandatory C smoke asserts loading-byte refusal, interruption before aggregate
output, ordered traversal interruption, cancellation after the first native reader
row, callback clearing/recovery, and an EXPLAIN QUERY PLAN indexed ID seek for
`json_each($ids) CROSS JOIN collection ON collection.id = hit_id`. It prints the
actual plan and rejects a collection scan. No seek-plan runtime result is claimed
until this patched artifact passes its coordinated build.
