# 0014: Turso storage engine naming

- Status: accepted (TS-786)
- Scope: TerraBase-owned engine identifiers; no behavior change
- Supersedes: [0002: SQLite storage engine](0002-sqlite-storage-engine.md)

## Decision

TerraBase's existing storage engine is **Turso**. TS-786 names its implementation
`src/TerraBase/Engine/Turso/`, namespace `TerraBase.Engine.Turso`, and uses `Turso*`
for TerraBase-owned engine types (for example `TursoStorageEngine`,
`TursoDatabaseStore`, `TursoStorageTransaction` and `TursoStatements`). The
`Engine/` interfaces remain the boundary for callers. This is a mechanical rename
of the existing implementation, not an engine replacement or dependency upgrade.

The driver is already the vendored **`Turso.Data.Sqlite`** project, referenced by
[`TerraBase.csproj`](../../../src/TerraBase/TerraBase.csproj), with `Turso.Data` and
`Turso.Raw`. Its upstream tag is **v0.8.2**; the Rust runtime comes from
**`Turso.Data.Native` 0.8.2**, pinned in
[`Directory.Packages.props`](../../../Directory.Packages.props). See the
[vendoring record](../../../src/vendor/Turso.Data.Sqlite/UPSTREAM.md).
`Turso.Data.Sqlite.Provider` is the upstream NuGet equivalent, not TerraBase's
current driver package reference.

## Intentional naming exceptions

- Upstream driver names stay unchanged: `Turso.Data.Sqlite`, `SqliteConnection`,
  `SqliteCommand`, `SqliteException` and `SqliteErrorCode` describe that API.
- SQL catalog names such as `sqlite_schema` and `sqlite_stat1`, the `sqlite_*`
  prefix, and SQLite error-code names/numbers remain engine protocol vocabulary.
  The SQL parser keeps its `SQLiteDialect` and SQLite-compatible type/SQL semantics.
- Import's `SqliteFile` reads the **SQLite 3 file format**, including its
  `SQLite format 3` header; it is not the TerraBase storage implementation and
  keeps its format-specific naming.

## Historical notes and compatibility

The SQLite/Microsoft.Data.Sqlite engine descriptions in
[anna-core](../design/anna-core.md), [query-plan notes](../sqlite-query-plans.md),
[CI notes](../ci.md), and `src/TerraBase/AGENTS.md` are historical where they
conflict with this record. Read their `Engine/Sqlite` paths and TerraBase-owned
`Sqlite*` engine identifiers as `Engine/Turso` and `Turso*`; the query-plan notes'
SQL and intentional upstream names are not subject to that substitution.
Their historical format changes do not describe a new change in TS-786.

On-disk paths (`.db`, including `_system.db`), table/index names, SQL statements,
and stored data are unchanged. `StorageFormat.Version` and `StorageFormat.Id`
are unchanged: **no format bump, migration or storage reset is required** for
this naming-only change.
