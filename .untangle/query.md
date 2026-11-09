# Untangle decision log — query slice (Sql, Postgres, Shapes)

Worker: query (branch `terrabase-modernize-query`). Additions chosen: Comments + Tests.
Method: `skills/untangle-codebase/SKILL.md` steps 2–5. C# rules: `skills/csharp/SKILL.md`.
Sources: my own full read of Sql/Postgres folders + three subagent sweeps (Sql, Postgres; Shapes pending).

## Fixes (chosen for this pass; each keeps behavior identical)

### Fix A — classify once
Call `SqlScript.Classify` / `Plan` one time per decision and pattern-match the union.
- `Sql/SqlService.cs:290` + `:334` — Classify/Plan called twice (condition + body).
- `Sql/ReplicaSqlSession.cs:53` + `:144` — same double-Classify pattern.

### Fix B — one result mapper
- `SqlStatementBody` mapping triplicated: `Sql/SqlBatch.cs:115`, `Sql/SqlExecuteEndpoint.cs:235`, `:322` → one static mapper.
- `Sql/SqlService.cs:113` and `:730` both build `SqlResult` from `SqlStatementResult` → one helper.
- `Sql/SqlService.cs:78/:451` (non-finite args) and `:108/:725` (non-finite rows) duplicated checks → one helper.
- `Sql/SqlService.cs:88` and `:690` duplicate `SqlStatement` construction (Bound map + named dict) → one helper.
- `Sql/SqlBatch.cs:77-91` duplicated `!IsAutocommit → ROLLBACK` cleanup blocks → one path.

### Fix C — named Postgres protocol constants
- OID magic numbers across `PostgresStatements.cs` (`Oid`, `Binary`, encode/decode arms), `PgCatalog.cs` (`Types`, `TypeName`), `PostgresTemporal.cs` → named constants in one `PgOid`-style home.
- `Protocol30 = 196608` copied: `PostgresConnectionHandler.cs:30`, `PostgresRelay.cs:15` → one constant.
- `PostgresConnectionHandler.cs:199` comment names AuthenticationCleartextPassword → named constant instead.

### Fix D — source-generated regex
- `PgNumeric.cs:11` `new Regex(..., Compiled)` → `[GeneratedRegex]` on a partial class.

### Fix E — no fully qualified names
`Sql/Routing/SchemaFacts.cs:64,77`; `Sql/SqlScript.cs:335`; `Sql/SqlExecuteEndpoint.cs:251`; `Sql/SqlSocket.cs:60,135,158-162`; `Sql/SqlSocketEndpoint.cs:93`; `Sql/SqlExtensions.cs:12-13`; `ISqlService.cs:75,79`; `SqlService.cs:18`; `Postgres/PostgresConnectionHandler.cs:357,390,420,801`; `Shapes/ShapeRoute.cs:134` (return type alias).

### Fix F — exhaustive switches on owned closed sets
- `Sql/Routing/WriteRouter.cs:262` `default:` secretly means delete → explicit case.
- `Sql/SqlScript.cs:227` `_ =>` is DELETE → explicit.
- `Sql/SqlModels.cs:101` `SqlRowsConverter` switch on owned `SqlValueKind` with default → exhaustive.
- `Postgres/PostgresConnectionHandler.cs:844` `_ => "ROLLBACK"` → explicit; `:576` unreachable `return null` after exhaustive switch → delete; `:225` if/is chain on `Access` union → switch; `:452-468` duplicated 'P'..'C' arms → one arm with `if (!failed)`.
- `Postgres/PostgresStatements.cs:139` `_ =>` on PgKind → explicit non-Sql arm retained (kind set open across files; PgKind.Sql named).

### Fix G — comments (TerraBase: none, keep outside-the-code only)
326 comment lines in slice. Keep: comments about PostgreSQL/SQLite/Npgsql/psql/protocol behavior and RFC facts (e.g. `SqlSocketEndpoint.cs:23` RFC 8441; `PostgresStatements.cs:166` SQLite LIKE; PgCatalog psql notes). Remove: everything restating code, all `///` summaries, `<inheritdoc />`, decision-ref comments (PIVOT F7, R10) — the code and docs own those.

### Fix H — one request-clock read
- `Sql/SqlService.cs:411` and `:433` both compute `Min(_transportStarted ?? max, _scriptStarted ?? max)`.

### Fix I — one busy translation in Shapes
- `Shapes/ShapeService.cs:56,81,99,119,139,153` repeat `catch (…Disposed or StorageBusy or QosMemory) → new Unavailable("shape storage changed or is busy; retry later")` → shared filter + shared instance.

### Fix J — Sql cleanups
- `Sql/ReplicaSqlSession.cs:416` dead `return (null, HomeSessions.Unreachable(home))` → delete.
- `Sql/SqlModels.cs:72` `?? new Dictionary<...>()` per call → `?? []`.
- Identifier quoting `"`→`""` in 5 places (`SchemaFacts.cs:57,83`, `SqlSandbox.cs:165`, `SqlDump.cs:188`, `SqlService.cs:173`) → one `SqlScript.Quote` helper.
- SHA256-hex tail of `SchemaFacts.Hash` / `WriteRouter.Fingerprint` → one helper.

### Fix K — Postgres cleanups
- `PostgresConnectionHandler.cs:630` re-inlines `Format()`; `:928` `$"…"` without placeholders; `:604,640` "at most 256" text vs `MaxNamed`; SET refusal prose `PostgresStatements.cs:388` re-lists `Settable` → derive.
- Pass-through property boilerplate on `Exchange`/`Prepared`/`Portal` (`PostgresConnectionHandler.cs:1054-1091`) → positional records.

### C# modernization list (applied by the fixes above)
- `[GeneratedRegex]` (Fix D). Exhaustive switch expressions on owned enums/unions (Fix F). Collection expressions where missing (`SqlModels.cs:72` etc.). Named constants for protocol numbers (Fix C). No fully qualified names (Fix E). Records instead of property bags (Fix K).

## Not fixed this pass (recorded for the coordinator / next pass)
- 4-layer re-classification of the same SQL text (SqlBatch → endpoint → session → service) — needs a threaded `SqlStatementPlan`; large, cross-cutting.
- `SqlRouteFacts.Kind` crosses the native FFI as strings — enum belongs in the Rust parser contract.
- `SqlExecuteEndpoint` reimplements ReplicaSqlSession's read/write fork (endpoint-level orchestration).
- `SqlSocket`'s 11 `GetRequiredService` lookups → constructor injection.
- `ISqlSession` no-op default interface members; `SqlBatch`'s `is ReplicaSqlSession` type test.
- Keyed `sql-transaction` TimeProvider registers the same instance as default (`SqlExtensions.cs:11`) — never varies.
- `_ts_conflicts` column list in `SqlSandbox.cs:22-29` duplicates HomeApplier's DDL (cross-slice).
- `SqlSandbox.Hidden` fabricates engine message text matched by `SqlErrors.Error`'s `StartsWith` (cross-slice, engine coupling).
- Error-classification by `StartsWith` on Turso message wording (`SqlErrors.cs:75-82`; engine slice).
- PgType universe encoded 6× (full single-table rewrite too large this pass; constants named instead).
- `Settable`/`Reported`/`Settings()` three-list consistency (`PostgresStatements.cs:48-70`).
- Relay wire framing duplicates PgWriter (`PostgresRelay.cs:113-135`); `CleartextRequest` byte-literal.
- `Portal.Pending` read-charge never accepted on the discard path (`PostgresConnectionHandler.cs:713-716`) — potential metering gap, cross-checked with QoS owner; not changed (behavior).
- Whole-cache `.Clear()` eviction races (`SchemaFacts.cs:34`, `WriteRouter.cs:107`).
- WITHOUT ROWID detected two ways (`SchemaFacts` tokens vs `SqlDump` substring).
- Message texts hardcode limit numbers ("5 seconds", "1 to 256", "16 KiB") — left identical to avoid wire-visible drift.

## CROSS-SLICE (for the coordinator)
1. `DatabaseRoute.CheckTenantAsync` boilerplate switch (Validation/NotFound/Unavailable → ErrorResponse.From) repeated in every endpoint across features; the check order differs between endpoints (execute: tenant→parameters; export/socket: parameters→tenant). One `DatabaseRoute` helper or TerraScale.Endpoints middleware removes ~6 copies per feature.
2. `HttpContext.Features.Get<QosRequestLease>()?.Lease.Dispose()` convention in long-lived-connection endpoints (`SqlSocketEndpoint.cs:80` + 4 other features) — QoS internals leaked into endpoints.
3. `_ts_conflicts` schema: `SqlSandbox.cs:22-29` must match `Features/HomeApply/HomeApplier.cs:27` DDL — one shared constant list (owner: whichever of us lands second should rebase).
4. Engine message wording drives error codes (`SqlErrors.cs:75-82` `StartsWith` mapping, `SqlSandbox.cs:50` fabricated "no such table") — engine + query slices should agree on typed failures instead.
5. `ISqlSession.Prefer` parses the `TerraScale-Write` header via `SqlExecuteEndpoint.Preference` from the Postgres handler and SqlSocket too (`PostgresConnectionHandler.cs:801`, `SqlSocket.cs:60`): the header parsing belongs to a transport-neutral type (Sql owns it, but naming lives on an endpoint class other features import).
6. Usage/request-log label `"OTHER"` for SQL over HTTP/WS/PG (`SqlSocket.cs:401,405`, `PostgresConnectionHandler.cs:500,502`) — metering names owned by platform slice; confirm intent.

## Test plan (do last)
- Survey `tests/TerraBase.{Fast,Slow}/{Sql,Postgres,Shapes}` + `TestHarness/Sql` for tests that can't fail or are tied to internals; rewrite to behavior. Baseline Fast: 88 tests, 12.1 s, all green.

## Work log
- Baseline build: 0 warnings / 0 errors. Baseline `make terrabase-test-fast`: passed, 88 tests, 12.078 s.
