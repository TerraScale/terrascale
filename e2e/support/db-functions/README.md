# C35 database function matrix

This is a capability inventory and a repeatable database journey. A generated table reports executed assertions, not a declaration that production is healthy. Every SQL cell runs, including PostgreSQL json-text-arrow. Every unexpected error is FAIL, including DDL serialization conflicts; later cells continue running and any FAIL or BLOCKED cell makes the process fail. There are no passing ownership exclusions. No request is retried by the raw SQL transports. REST corpus reads use `TerraScale-Write: home` because independent replica reads are eventually consistent; WebSocket and PostgreSQL sessions exercise their read-your-writes contract.

## Run

The two-region journey uses the same corpus as the production driver:

```sh
export E2E_REGIONS=2
bash e2e/env/local.sh prepare
bash e2e/env/local.sh start
bash e2e/env/local.sh exec bash e2e/env/up.sh
cd e2e
bash env/local.sh exec npx playwright test placement/write-guarantees.spec.ts --project placement --grep 'SQL functions through'
bash env/local.sh stop
```

On NixOS wrap the commands with `nix shell nixpkgs#nss.tools nixpkgs#postgresql -c`. The local fixture publishes its scratch organization's Pro request budget for each SQL lane and restores Hobby afterward; production requires an existing Pro organization. The placement fixture owns the scratch database and the setup/close projects own its scratch account. The runner takes credentials only on stdin and registers generated local secrets with the journey scrubber. Local Postgres connections use the journey's registered listener ports; production connections use TLS with certificate verification.

After operator sign-in, `node e2e/support/db-functions/production.mjs` creates its own uniquely named scratch project, three-region database and read-write key. It runs the SQL matrix, runs the management, fork, recovery, shape, export/import and replication corpus, compares the recovered data and deletes all databases in its own project before deleting that project. It captures CLI secrets in memory. A cleanup failure leaves a private journal containing only the project and organization IDs. An optional positional argument selects a CLI executable built from this checkout when the installed CLI is older.

`node e2e/support/db-functions/run.mjs production` also reads `{projectId,databaseId,apiKey}` from a private stdin file belonging to a newly created scratch project and database. It exercises gru, sjc and fra, and leaves lifecycle ownership to the caller. Never provide a customer project.

Production Postgres connects to each regional API host's IPv4 address on port 5432 while verifying `pg.terrascale.tech`. This proves the selected ingress address; verifying that these addresses identify the intended regional edges remains an operator prerequisite. It does not prove that a session executes locally rather than being relayed to the writer.

## Code and documentation inventory

`D` below is `/v1/projects/{project}/databases/{database}`. `execute` means `POST D/sql/execute`; `ws` means `GET D/sql/ws` with `terrascale.sql.v1`. PostgreSQL uses the `{project}/{database}` startup parameter. Management and recovery have no SQL WebSocket message or PostgreSQL command; their cells are not applicable on those transports.

| Capability | REST | WebSocket | PostgreSQL | Producer and promise | Coverage in this corpus |
| --- | --- | --- | --- | --- | --- |
| Create, inspect, list, delete databases | `POST /v1/projects/{project}/databases`, `GET/DELETE D` | Not applicable | Not applicable | `Features/Management/*ManagedDatabaseEndpoint.cs`, `concepts/sql.md` | Placement fixture create/delete; production lifecycle pending |
| Read-write and read-only keys | ts-auth `/v1/terrascale/api-keys/self/scoped-keys` | Auth frame then execute | Password startup then SQL | `ScopedKeyEndpoint.cs`, `PostgresConnectionHandler.cs`, `guides/agent-databases.md` | Scoped read key, readable SELECT, refused write, unchanged data |
| Tables: CREATE, ALTER, DROP | execute | execute | Simple/extended query | `SqlScript.cs`, `SqlSandbox.cs`, `PostgresStatements.cs`, `concepts/sql.md` | Create, add/rename column, drop, persisted data, all nine allowed schema PRAGMAs |
| Indexes: ordinary, partial, expression | execute | execute | Simple/extended query | Same SQL entry points | Create/drop partial index; create expression index |
| Views and triggers | execute | execute | Simple/extended query | `SqlSandbox.RewriteSchema`, `concepts/sql.md` | Create/query/drop view; create/fire/read/drop trigger |
| ALTER index/view/trigger | execute refusal | execute refusal | Query refusal | SQLite dialect has ALTER TABLE; these ALTER forms are not promised | Explicit ALTER INDEX/VIEW/TRIGGER syntax refusals |
| INSERT, UPDATE, DELETE, UPSERT, RETURNING | execute | execute | Simple/extended query | `SqlService.cs`, `SqlBatch.cs`, `concepts/sql.md` | Returned values and persisted row counts |
| Joins, aggregates, CTEs | execute | execute | Simple/extended query | `SqlScript.cs`, `PostgresStatements.cs` | Join data, count/sum/min/max, CTE sum, FULL JOIN refusal |
| JSON and pagination | execute with args | execute with args | Extended query | `concepts/sql.md` | `json_extract`, `->`, `->>`, `json_each`, ordered LIMIT/OFFSET |
| Null, boolean, integer, real, text, blob parameters | `args`/`named` | execute `args`/`named` | Parse/Bind/Execute | `SqlJson.Value`, `PostgresStatements.Decode` | Bind into typed table; verify storage types and blob bytes; named arguments |
| Exact int64, UUID and temporal parameters | args, SDK BigInt | execute args | Typed text/binary Bind | `PgOid.cs`, `PostgresTemporal.cs`, `concepts/sql.md` | SDK int64 extrema; 17 binary OIDs including UUID/date/timestamps/JSON/numeric; malformed lengths refused |
| Multi-statement request atomicity | statements array | execute statements | Simple-query script | `SqlBatch.cs`, `SqlScript.cs`, `PostgresConnectionHandler.cs` | REST/WS batch commits, refused statement rollback; PostgreSQL simple-script atomicity |
| Commit and rollback | Request commit; explicit control refused | begin/commit/rollback | BEGIN/COMMIT/ROLLBACK | `SqlSocket.cs`, `PostgresStatements.cs` | WS/pg commit, rollback, savepoint rollback/release and aborted-state assertions |
| Constraint and SQL errors | error code/sqlstate/status | Error frame code/sqlstate | ErrorResponse SQLSTATE | `SqlErrors.cs`, `guides/errors.md` | PK, unique, not-null, check, FK and ON DELETE CASCADE, missing table/column, syntax, sandbox |
| Concurrent write conflict | execute | Transaction sessions | Transaction sessions | `SqlSession`, `SqlErrors.SerializationConflict` | Overlapping home/remote WS and pg transactions; loser receives 40001; winner data preserved |
| Cross-region read-after-write and lag | execute, `GET D/regions` | New execute session | New query session | `ReplicaReads.cs`, `ReplicaSqlSession.cs`, `guides/replication.md` | Regional lanes; report replica lag, accept old/new immediate replica state, require fresh home-routed read |
| Large rows and size boundaries | 1 MiB request, 256 statements | Bounded execute frames | Bounded Parse/Bind/result | `SqlBatch.cs`, `SqlScript.cs`, `concepts/sql.md` | 512 KiB stored row; exact/overflow request, SQL, row-count, result-byte, transaction-write and batch boundaries |
| Snapshot backups and restore | `POST/GET D/backups`, `POST D/backups/{backup}/restore`, `GET D/restores/{operation}` | Not applicable | Not applicable | `Features/Management/Backups`, `guides/backups-and-restore.md` | Snapshot creation/listing; restored data, index, view and trigger assertions |
| PITR plan, restore and rewind | `D/point-in-time`, `/plan`, `/restore`, `/rewind` | Not applicable | Not applicable | Same backup endpoints and guide | Published window and plan; PITR restore and rewind data/schema/trigger assertions; old shape handle invalidation |
| Export/import | `GET D/export`; import uses execute batches | Execute batches for imported SQL | SQL dump script | `SqlDump.cs`, `ExportDatabaseEndpoint.cs`, CLI `import.go` | SDK export stream consumed; CLI dump imported into scratch copy with trigger execution |
| Fork, rename, placement and TTL | `POST D/branches`, `PATCH D`, `D/regions`, `D/ttl` | Not applicable | Not applicable | `Features/Management`, `guides/agent-databases.md` | Fork isolation, rename, TTL create/change/clear; existing placement journey. Actual TTL expiry is not timed here |
| Shapes and table grants | `D/tables/{table}/shape` and grants | Shape hint stream, distinct from SQL ws | Not applicable | `Features/Shapes`, `concepts/sql.md` | Raw snapshot bootstrap, SDK snapshot/live data and table grant create/read/delete, including after rewind |
| Request/work budgets and sandbox | execute refusal | Error frame/transaction state | ErrorResponse/transaction state | `SqlLimits.cs`, `SqlSandbox.cs`, `guides/errors.md` | Denied attach/detach/extensions/files/internal/temporary/virtual/pragma/maintenance operations; work-budget 57014 |

SQL corpus cases are in `cases.mjs` and `advanced.mjs`; management, recovery, shapes and replication are in `management.mjs`; raw transport implementations are in `transports.mjs`; `run.mjs` emits one JSON receipt per assertion followed by a Markdown count table. `placement/write-guarantees.spec.ts` runs one scratch database per regional transport through the existing two-region journey. SDK-only paths use this checkout's JavaScript SDK; this does not qualify a published package version.

## Evidence still required

No production SQL assertion has run yet: the pool hosts lack a signed-in operator CLI. COS is arranging sign-in on delta. Production database lifecycle, ingress identity, post-deploy qualification and final scratch-data cleanup receipts run in this thread after merge and release, once operator sign-in is available.

## Reproduced roots

PostgreSQL trigger splitting treated every semicolon as a statement boundary, including statements inside a SQLite trigger body. The splitter now keeps BEGIN/END bodies and nested CASE/END expressions together; the journey verifies both writes of the trigger.

PostgreSQL inferred parameter OIDs only in Describe. Drivers that send Parse/Bind/Describe(portal)/Execute therefore decoded a binary blob as untyped text. Inference now runs during Parse, before Bind, and the journey verifies every stored parameter type and the blob bytes. The earlier two-region run exposed a separate fra root: `ListTablesAsync` opens a separate replica snapshot without the session position, so it can miss the table just created by that session. The session metadata reader now uses the session position or its existing transaction without opening a user transaction. Both fra parameter cells pass, as do metadata reads before the first user write and inside uncommitted DDL.

SQL JSON `->>` still fails on PostgreSQL. SqlParserCS 0.6.5's upstream `Tokenizer.TokenizeLongArrow` advances past the operator, then `ConsumeForBinOp` advances again, consuming the following character. With a directly adjacent quoted JSON path, the producer reports an unterminated string. The package has no newer stable release. COS assigned the source/import and upstream fix to C35b. The PostgreSQL cells now execute and fail until C35b lands; the parser is unchanged here.

CLI export/import had three roots: API-key dispatch bypassed export/import, exported BEGIN/COMMIT conflicted with the REST batch transaction, and statement splitting broke trigger bodies. Shared dispatch, a validated dump envelope and the existing schema tokenizer now preserve the complete round trip.

Rewind treated commit_offset as user data, replacing the target incarnation with the recovery database incarnation while keeping the target store identity. It also bypassed commit notification, retaining cached shape lineage. Rewind now preserves the target counter, atomically renews the incarnation and publishes the commit; the journey checks stale-handle rejection and fresh snapshot/live data.

Historical head 6d0f237 recorded 542 SQL passes and two nonexecuted JSON ownership cells. Review round 1 removes those exclusions; use the current receipts and PR table for executable pass/fail counts. The management/recovery corpus adds 16 passing assertions, including rewind shape invalidation and fresh live data. Production evidence remains pending.

Review coverage includes supported pg_catalog table/column introspection, uncommitted DDL visibility and rollback, an explicit 0A000 refusal for unsupported information_schema.columns, Parse/Describe(statement)/binary Bind/Execute with named-statement reuse, and simple-query trigger bodies with quoted semicolons and line/block comments followed by additional statements. PgCatalog.Answer intentionally supports psql's catalog queries, not arbitrary information_schema SQL.

The PostgreSQL lane also reports three latency samples for parameterized scalar reads inside home-routed transactions with 200 tables. Such statements now avoid schema lookup because they have no table reference; table-dependent inference remains session-consistent. Replica reader connections set foreign_keys=ON before query_only so their documented PRAGMA agrees with enforced write semantics.
