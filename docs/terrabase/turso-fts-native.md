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
matching expression in `fts_match`/`fts_score` in a separate process. Rejection
is diagnostic and does not fail an artifact whose derived text-column FTS
passed. The engine uses ordinary derived text columns.

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
Runtime smoke, Release/Fast and two-run SHA equality still require actual builds.
The old security audit describes the replaced NuGet payload; its documented
crossbeam-epoch risk is not claimed resolved by this change.

Needs format bump. The deploy lane owns `StorageFormat.cs`.
