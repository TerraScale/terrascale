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

## Round 2 additions (coordinator list)

Landed:
- One TimeProvider: deleted keyed `sql-transaction` registration; SqlService uses the default clock; the hardening test overrides the default TimeProvider for its server instance.
- WritePreference: TerraScale-Write parsing moved off SqlExecuteEndpoint to a transport-neutral type used by HTTP, Postgres wire and SqlSocket.
- One GUC table in PostgresStatements: Settable, Reported and Settings derive from one ordered table; both wire orders (SET refusal text, ParameterStatus order) are preserved exactly.
- One WITHOUT ROWID detection: SchemaDdl.WithoutRowid (token-based) used by SchemaFacts and SqlDump.
- Bounded FIFO eviction replaces whole-cache .Clear() in SchemaFactsCache and WriteRouter fingerprints.
- Portal read charges accepted on Close and DISCARD ALL (metering fix, behavior change).
- PgWriter extracted to feature scope; relay refusal reuses it (identical S/V/C/M framing).

Assessed, deliberately not done this round (each is a large cross-cutting redesign; recorded for the coordinator):
- Threading one SqlStatementPlan through SqlBatch/endpoint/session/service: SqlCommand is built from raw SQL at ~10 sites (Postgres handler, batch, endpoints); threading the plan changes ISqlSession's surface and every constructor. Proposal: add `SqlStatementPlan? Plan` to SqlCommand filled by a single factory and read by ReplicaSqlSession/WriteRouter instead of re-classifying.
- Merging SqlExecuteEndpoint's fork into ReplicaSqlSession: the endpoint's fork owns HTTP-specific forwarding (raw body passthrough, push-through settlement, route header, local-commit fast path); the session's fork owns per-connection transaction state. Merging entangles HTTP forwarding with session state for no duplication removed beyond the shape of the fork.
- ISqlSession no-op DIMs + `is ReplicaSqlSession` type test: the defaults express real semantics (local sessions don't track routes, committed rows or replica batches); removing them moves the no-ops into SqlSession without deleting anything. Left as-is on purpose.
- PgType universe encoded 6x: full single-table rewrite is wire-sensitive and was consciously deferred in round 1; unchanged.
