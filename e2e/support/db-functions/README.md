# C35 database function matrix

This is a capability inventory and a repeatable SQL journey. A generated table reports executed assertions, not a declaration that production is healthy. `C34` identifies a DDL serialization conflict; its dependent assertions remain blocked. No request is retried by the raw SQL transports. REST corpus reads use `TerraScale-Write: home` because independent replica reads are eventually consistent; WebSocket and PostgreSQL sessions exercise their read-your-writes contract.

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

On NixOS wrap the commands with `nix shell nixpkgs#nss.tools nixpkgs#postgresql -c`. The placement fixture owns the scratch database and the setup/close projects own its scratch account. The runner takes credentials only on stdin and registers generated local secrets with the journey scrubber. Local Postgres connections use the journey's registered listener ports; production connections use TLS with certificate verification.

After operator sign-in, `node e2e/support/db-functions/production.mjs` creates its own uniquely named scratch project, three-region database and read-write key. It runs the SQL matrix, snapshots/restores the database, exports/imports SQL, compares the recovered data and deletes all databases in its own project before deleting that project. It captures CLI secrets in memory. A cleanup failure leaves a private journal containing only the project and organization IDs. An optional positional argument selects a CLI executable built from this checkout when the installed CLI is older.

`node e2e/support/db-functions/run.mjs production` also reads `{projectId,databaseId,apiKey}` from a private stdin file belonging to a newly created scratch project and database. It exercises gru, sjc and fra, and leaves lifecycle ownership to the caller. Never provide a customer project.

Production Postgres connects to each regional API host's IPv4 address on port 5432 while verifying `pg.terrascale.tech`. This proves the selected ingress address; verifying that these addresses identify the intended regional edges remains an operator prerequisite. It does not prove that a session executes locally rather than being relayed to the writer.

## Code and documentation inventory

`D` below is `/v1/projects/{project}/databases/{database}`. `execute` means `POST D/sql/execute`; `ws` means `GET D/sql/ws` with `terrascale.sql.v1`. PostgreSQL uses the `{project}/{database}` startup parameter. Management and recovery have no SQL WebSocket message or PostgreSQL command; their cells are not applicable on those transports.

| Capability | REST | WebSocket | PostgreSQL | Producer and promise | Coverage in this corpus |
| --- | --- | --- | --- | --- | --- |
| Create, inspect, list, delete databases | `POST /v1/projects/{project}/databases`, `GET/DELETE D` | Not applicable | Not applicable | `Features/Management/*ManagedDatabaseEndpoint.cs`, `concepts/sql.md` | Placement fixture create/delete; production lifecycle pending |
| Read-write and read-only keys | ts-auth `/v1/terrascale/api-keys/self/scoped-keys` | Auth frame then execute | Password startup then SQL | `ScopedKeyEndpoint.cs`, `PostgresConnectionHandler.cs`, `guides/agent-databases.md` | Scoped read key, readable SELECT, refused write, unchanged data |
| Tables: CREATE, ALTER, DROP | execute | execute | Simple/extended query | `SqlScript.cs`, `SqlSandbox.cs`, `PostgresStatements.cs`, `concepts/sql.md` | Create, add/rename column, drop, persisted data |
| Indexes: ordinary, partial, expression | execute | execute | Simple/extended query | Same SQL entry points | Create/drop partial index; create expression index |
| Views and triggers | execute | execute | Simple/extended query | `SqlSandbox.RewriteSchema`, `concepts/sql.md` | Create/query/drop view; create/fire/read/drop trigger |
| ALTER index/view/trigger | execute refusal | execute refusal | Query refusal | SQLite dialect has ALTER TABLE; these ALTER forms are not promised | Pending explicit refusal cases |
| INSERT, UPDATE, DELETE, UPSERT, RETURNING | execute | execute | Simple/extended query | `SqlService.cs`, `SqlBatch.cs`, `concepts/sql.md` | Returned values and persisted row counts |
| Joins, aggregates, CTEs | execute | execute | Simple/extended query | `SqlScript.cs`, `PostgresStatements.cs` | Join data, count/sum/min/max, CTE sum, FULL JOIN refusal |
| JSON and pagination | execute with args | execute with args | Extended query | `concepts/sql.md` | `json_extract`, `->`, `->>`, `json_each`, ordered LIMIT/OFFSET |
| Null, boolean, integer, real, text, blob parameters | `args`/`named` | execute `args`/`named` | Parse/Bind/Execute | `SqlJson.Value`, `PostgresStatements.Decode` | Bind into typed table; verify storage types and blob bytes; named arguments |
| Exact int64, UUID and temporal parameters | args, SDK BigInt | execute args | Typed text/binary Bind | `PgOid.cs`, `PostgresTemporal.cs`, `concepts/sql.md` | SDK int64 extrema; UUID, temporal and all binary OIDs pending |
| Multi-statement request atomicity | statements array | execute statements | Simple-query script | `SqlBatch.cs`, `SqlScript.cs`, `PostgresConnectionHandler.cs` | REST/WS batch commits; refused second statement leaves first absent; pg script pending |
| Commit and rollback | Request commit; explicit control refused | begin/commit/rollback | BEGIN/COMMIT/ROLLBACK | `SqlSocket.cs`, `PostgresStatements.cs` | WS/pg commit and rollback row assertions; savepoints and aborted transaction pending |
| Constraint and SQL errors | error code/sqlstate/status | Error frame code/sqlstate | ErrorResponse SQLSTATE | `SqlErrors.cs`, `guides/errors.md` | PK, unique, not-null, check, FK, missing table/column, syntax, sandbox |
| Concurrent write conflict | execute | Transaction sessions | Transaction sessions | `SqlSession`, `SqlErrors.SerializationConflict` | Pending overlapping writers with observed 40001 |
| Cross-region read-after-write and lag | execute, `GET D/regions` | New execute session | New query session | `ReplicaReads.cs`, `ReplicaSqlSession.cs`, `guides/replication.md` | Regional lanes; immediate cross-region visibility/lag assertions pending |
| Large rows and size boundaries | 1 MiB request, 256 statements | Bounded execute frames | Bounded Parse/Bind/result | `SqlBatch.cs`, `SqlScript.cs`, `concepts/sql.md` | 256 KiB computed result; actual large stored row and boundary cases pending |
| Snapshot backups and restore | `POST/GET D/backups`, `POST D/backups/{backup}/restore`, `GET D/restores/{operation}` | Not applicable | Not applicable | `Features/Management/Backups`, `guides/backups-and-restore.md` | Operator driver snapshots, mutates and compares restored data; execution pending |
| PITR plan, restore and rewind | `D/point-in-time`, `/plan`, `/restore`, `/rewind` | Not applicable | Not applicable | Same backup endpoints and guide | Pending proven window and restored table/index/view/trigger data |
| Export/import | `GET D/export`; import uses execute batches | Execute batches for imported SQL | SQL dump script | `SqlDump.cs`, `ExportDatabaseEndpoint.cs`, CLI `import.go` | SDK export stream consumed and SQL envelope checked; operator driver round trip execution pending |
| Fork, rename, placement and TTL | `POST D/branches`, `PATCH D`, `D/regions`, `D/ttl` | Not applicable | Not applicable | `Features/Management`, `guides/agent-databases.md` | Existing placement journey; dedicated matrix cells pending |
| Shapes and table grants | `D/tables/{table}/shape` and grants | Shape hint stream, distinct from SQL ws | Not applicable | `Features/Shapes`, `concepts/sql.md` | Existing shape journey; dedicated matrix cells pending |
| Request/work budgets and sandbox | execute refusal | Error frame/transaction state | ErrorResponse/transaction state | `SqlLimits.cs`, `SqlSandbox.cs`, `guides/errors.md` | ATTACH and FULL OUTER JOIN refusal; complete denied statements and budgets pending |

SQL corpus cases are in `cases.mjs`; raw transport implementations are in `transports.mjs`; `run.mjs` emits one JSON receipt per assertion followed by a Markdown count table. `placement/write-guarantees.spec.ts` runs one scratch database per regional transport through the existing two-region journey. SDK-only paths use this checkout's JavaScript SDK; this does not qualify a published package version.

## Evidence still required

No production SQL assertion has run yet: the pool hosts lack a signed-in operator CLI. COS is arranging sign-in on delta. Production database lifecycle, ingress identity, the pending inventory rows, post-deploy qualification and final scratch-data cleanup receipts must be completed before this work can leave draft.

## Reproduced roots

PostgreSQL trigger splitting treated every semicolon as a statement boundary, including statements inside a SQLite trigger body. The splitter now keeps BEGIN/END bodies and nested CASE/END expressions together; the journey verifies both writes of the trigger.

PostgreSQL inferred parameter OIDs only in Describe. Drivers that send Parse/Bind/Describe(portal)/Execute therefore decoded a binary blob as untyped text. Inference now runs during Parse, before Bind, and the journey verifies every stored parameter type and the blob bytes. This fixes the ordering defect, but the latest two-region run still fails the blob bind in fra: `ListTablesAsync` opens a separate replica snapshot without the session position, so it can miss the table just created by that session. Session-consistent metadata remains unresolved; ordinary SQL reads would also affect transaction read tracking, so that change needs a deliberate session contract.

SQL JSON `->>` still fails on PostgreSQL. SqlParserCS 0.6.5's upstream `Tokenizer.TokenizeLongArrow` advances past the operator, then `ConsumeForBinOp` advances again, consuming the following character. With a directly adjacent quoted JSON path, the producer reports an unterminated string. The package has no newer stable release. Replacing or modifying that upstream tokenizer requires a separate decision; no whitespace insertion, query rewrite, retry or relaxed assertion is used here. The failed case remains in the corpus.
