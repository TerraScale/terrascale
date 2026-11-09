# R2 query slice decision log

Slice: src/TerraBase/Features/{Sql,Postgres,Shapes} + tests/TerraBase.{Fast,Slow}/{Sql,Postgres,Shapes} + tests/TerraBase.TestHarness/Sql.
Baseline: origin/terrabase-r2 = 9fd54147e (build clean; Fast green per round 1).

## Fixes

- Delete every comment in the slice (src + tests), keeping code intact; external-constraint facts go to the THIRD-PARTY list in the final report.
  - 30 comment blocks in src (SqlSocketEndpoint RFC 8441; PostgresTemporal DateTime range; PostgresStatements SQLite LIKE, psql untyped binds, refused-command and BEGIN regexes; PostgresConnectionHandler PostgreSQL transaction semantics ×5; SqlExecuteEndpoint region doc ref; PgCatalog /// summary + psql notes ×6; ShapeSweeper #277; ShapeCdnAnalytics doc ref + Cloudflare lag; ShapeMirror #838 + retry design ×3; ShapeCrypto doc ref; GetShapeHintEndpoint RFC 8441).
  - 23 comment blocks in tests (PostgresNumericTests Npgsql binary; PostgresWireTests psql \dt, implicit txn, quoting, TS refs ×4; SqlMeteringTests isolation; TableShapeTests gauge; ColdReplicaTests sweep ordering; SqlNativeRestTests plan budget; TestHarness SqlApi/SqlTenantFixture/SqlNativeDatabase summaries; TableShapeClient 429 retry; Slow Shapes ordering invariants ×6).
  - Workaround audit of each before deletion: ShapeMirror/ShapeCdnPurge bounded backoff against S3/Cloudflare = real design (stays); GetShapeHintEndpoint 1s poll delay = long-poll contract (stays); bare `catch` blocks in SqlSocket.cs:250, PostgresConnectionHandler.cs:245/1017, ShapeCrypto.cs:134 are release-and-rethrow cleanup, not swallowed errors (stay); SqlErrors StartsWith mapping is RESERVED.
- Move tests/TerraBase.Slow/Platform/WriteRouterTests.cs to tests/TerraBase.Slow/Sql/ with namespace TerraBase.Slow.Sql (round-1 note; tests this slice's router).
- SqlSocket: replace 11 per-call GetRequiredService lookups with constructor-injected dependencies resolved once per connection (round-1 remaining item). Call-site edit: SqlSocketEndpoint.cs.
- Replace every fully qualified `System.*` name in the slice (src + tests) with short names and per-file usings (self-check grep now empty; 18 files).
- Keyed `sql-transaction` TimeProvider (SqlExtensions.cs:11): KEEP — round 1 called it never-varying, but tests/TerraBase.Slow/Sql/SqlNativeHardeningTests.cs:197 replaces it with a fake clock; it is the sanctioned TimeProvider test seam, not a workaround.
- PostgresRelay framing vs PgWriter, WITHOUT ROWID dual detection, 4-layer statement re-classification, PgType 6× encoding, ISqlSession DIMs, SqlExecuteEndpoint read/write fork duplication: left alone — cross-file unification needs a new shared type or is explicitly reserved/cross-cutting; recorded for the coordinator, no patching around.

## Problems found and disposition

- No `[Obsolete]`/legacy/compat/fallback paths in the slice (grep clean).
- No unit tests in the slice's test folders: all suites drive TerraBaseTestServer over HTTP/WebSocket/Postgres wire; TestHarness types are HTTP clients/fixtures, not internal-type construction. Namespace audit: only WriteRouterTests misplaced (fix above).
