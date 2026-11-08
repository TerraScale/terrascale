# SQL SDK API shape

Every TerraScale SDK exposes the same SQL surface with the same names, adjusted only to the language's casing and
async conventions. The .NET SDK (`sdk/dotnet`, `TerraBaseSqlClient`) is the reference. Server contracts:
`docs/terrabase/sql/native-sql.md` and `docs/terrabase/sql/websocket.md`; recorded fixtures:
`sdk/dotnet/contracts/terrabase/sql/`.

## Client

| Concept | .NET | Notes |
|---|---|---|
| Client | `new TerraBaseSqlClient(http, options)` | options: `BaseAddress` or `Region` (default origin `https://api.terrascale.tech`), `ApiKey` or `AccessTokenProvider` |
| Database | `client.Database(projectId, database)` | name or ULID; no request |

## Databases for agents

One database per agent, session or test run: create it, fork it, let it expire, and hand the agent a key that reaches
only that database. Server contract: `docs/terrabase/agent-databases.md`; recorded fixtures:
`sdk/dotnet/contracts/terrabase/agent/`. Database routes need `databases:write` (create, fork, TTL, delete).

| Method | Request | Returns |
|---|---|---|
| `createDatabase(project, name, { ttlSeconds? })` | `POST /v1/projects/{project}/databases` `{"name","ttlSeconds"}` | `ManagedDatabase` (201) |
| `forkDatabase(project, source, name, { pointInTime?, ttlSeconds? })` | without `pointInTime`: `POST …/databases/{source}/branches` `{"name","ttlSeconds"}`; with it: `POST …/databases/{source}/point-in-time/restore` `{"name","pointInTime","ttlSeconds"}`, then poll `GET …/databases/{source}/restores/{operation}` until terminal and read the database | `ManagedDatabase` (201 branches; 202 + poll restore) |
| `setDatabaseTtl(project, database, ttlSeconds)` | `PUT …/databases/{database}/ttl` `{"ttlSeconds"}` | `ManagedDatabase` (200) |
| `clearDatabaseTtl(project, database)` | `DELETE …/databases/{database}/ttl` | `ManagedDatabase` (200) |
| `deleteDatabase(project, database)` | `DELETE …/databases/{database}` | nothing (204) |
| `createScopedKey({ project?, database, access, ttlSeconds? })` | `POST https://auth.terrascale.tech/v1/terrascale/api-keys/self/scoped-keys`, `Authorization: Basic base64(keyId:key)` of the client's `ApiKey`, `{"project","database","access","ttl_seconds"}` | `ScopedKey` (201) |

`ManagedDatabase` is the existing database body (`id`, `projectId`, `name`, `branchedFrom`, `createdAt`, `region`,
`homeRegion`, …) plus `expiresAt` (RFC 3339, or null without a TTL).

- `ttlSeconds` is 60 to 31,536,000 (365 days). The database, its backups and its data keys are deleted (crypto-shredded)
  within seconds after `expiresAt`; deletion is the same as `deleteDatabase`. A TTL database counts against the plan's
  database limit and is billed like any other until it is deleted. A fork does not inherit the source's TTL.
- `pointInTime` is an RFC 3339 instant inside the source's restore window; the fork holds every transaction committed
  before it. Without it the fork is a consistent copy taken now. `source` and `database` are a name or ULID.
- `createScopedKey` mints a new API key from the client's own key. `database` is the database ULID (never a name).
  `access` is `read` (scope `sql:read`) or `read_write` (`sql:read sql:write`). `ttl_seconds` is 60 to
  2,592,000 (30 days), default 3600; the key never outlives the parent key. `project` is required for an organization
  key and must equal the key's project for a project key. The parent must reach the database and hold the scopes.
  A scoped key cannot mint keys, manage databases or reach another database (404). Revoking or rotating the parent
  revokes its scoped keys. `ScopedKey` is ts-auth JSON in snake_case: `{"key","id","project_id","database","access","expires_at"}`, surfaced in each SDK's casing; `key` is shown once and
  is a normal API key string, so it goes straight into another client's `ApiKey` option.
- Errors keep each SDK's HTTP error types; ts-auth refusals are 400 `invalid_request`, 401 `invalid_client`, 403
  `forbidden` (scope or database beyond the parent's) and 429 with Retry-After.

| .NET | JS/TS | Go | Python | Rust |
|---|---|---|---|---|
| `client.CreateDatabaseAsync` | `client.createDatabase` | `client.CreateDatabase` | `client.create_database` | `client.create_database` |
| `client.ForkDatabaseAsync` | `client.forkDatabase` | `client.ForkDatabase` | `client.fork_database` | `client.fork_database` |
| `client.SetDatabaseTtlAsync` / `ClearDatabaseTtlAsync` | `setDatabaseTtl` / `clearDatabaseTtl` | `SetDatabaseTTL` / `ClearDatabaseTTL` | `set_database_ttl` / `clear_database_ttl` | `set_database_ttl` / `clear_database_ttl` |
| `client.DeleteDatabaseAsync` | `client.deleteDatabase` | `client.DeleteDatabase` | `client.delete_database` | `client.delete_database` |
| `client.CreateScopedKeyAsync` | `client.createScopedKey` | `client.CreateScopedKey` | `client.create_scoped_key` | `client.create_scoped_key` |

Durations are seconds in every SDK (`TimeSpan`/`Duration` accepted where idiomatic and sent as whole seconds).

## Placement: where a database lives

A database has one home region, which commits its writes, and replica regions, which hold read replicas. All-regions
mode places a replica in every serving region, including regions that join later. A database can move its home to one
of its replica regions. Server contract: `docs/terrabase/placement.md`; recorded fixtures:
`sdk/dotnet/contracts/terrabase/placement/`. Routes need `databases:write`.

| Method | Request | Returns |
|---|---|---|
| `createDatabase(project, name, { homeRegion?, replicaRegions?, allRegions?, ttlSeconds? })` | `POST /v1/projects/{project}/databases` `{"name","homeRegion","replicaRegions","allRegions","ttlSeconds"}` | `ManagedDatabase` (201) |
| `setDatabasePlacement(project, database, { replicaRegions?, allRegions? })` | `PATCH …/databases/{database}` `{"replicaRegions"}` or `{"allRegions"}` | `ManagedDatabase` (200) |
| `moveDatabaseHome(project, database, homeRegion)` | `POST …/databases/{database}/move-home` `{"homeRegion"}` | `ManagedDatabase` (200) |

`ManagedDatabase` carries `homeRegion`, `replicaRegions` (sorted; with `allRegions` every serving region other than the
home) and `allRegions` (boolean).

- Omitted options are not sent. `replicaRegions` names at most 8 distinct serving regions other than the home and
  turns all-regions mode off; `allRegions: true` turns it on. Sending both is refused, and `setDatabasePlacement` needs
  one of them.
- `moveDatabaseHome` waits until the move is done (seconds). `homeRegion` must be a replica region; the old home becomes
  a replica. Writes pause briefly while the home switches; no acknowledged write is lost.
- Placement requests go to the home region. Behind `api.terrascale.tech` the edge routes them; a direct regional call
  elsewhere answers `421 wrong_region` naming the home.
- Errors keep each SDK's HTTP error types: `400 invalid_request` (bad regions, target not a replica), `409
  move_in_progress`, `421 wrong_region`, `503 replica_not_ready` (the target replica could not catch up; nothing
  changed) and `503 move_unresolved` (the move outcome is being settled; retry the same call).

| .NET | JS/TS | Go | Python | Rust |
|---|---|---|---|---|
| `client.SetDatabasePlacementAsync` | `client.setDatabasePlacement` | `client.SetDatabasePlacement` | `client.set_database_placement` | `client.set_database_placement` |
| `client.MoveDatabaseHomeAsync` | `client.moveDatabaseHome` | `client.MoveDatabaseHome` | `client.move_database_home` | `client.move_database_home` |

## REST: one request, one transaction

`POST /v1/projects/{project}/databases/{database}/sql/execute`

| Method | Returns | Notes |
|---|---|---|
| `execute(sql, args?)` / `execute(sql, named)` / `execute(statement)` | `StatementResult` | one statement |
| `query(sql, args?)` / `query(sql, named)` | `Row[]` | rows of one statement |
| `query<T>(sql, args, mapper)` | `T[]` | typed rows; the row becomes an object keyed by column name, blobs as base64 strings |
| `batch(statements)` | `StatementResult[]` | 1 to 256 statements, all or nothing, one result per statement |

## WebSocket: interactive transactions

`GET /v1/projects/{project}/databases/{database}/sql/ws`, subprotocol `terrascale.sql.v1`.

| Method | Notes |
|---|---|
| `database.connect()` | returns a `Session` the caller closes |
| `session.execute` / `query` / `query<T>` / `batch` | same signatures as REST; one transaction each outside a transaction |
| `session.begin()` / `commit()` / `rollback()` | frames `begin`, `commit`, `rollback`; a transaction lasts at most 5 seconds |
| `session.transaction(fn)` | begin, run `fn(session)`, commit; roll back and rethrow when `fn` throws |
| `session.inTransaction` | the last answer's `transaction` flag |
| `session.close()` | normal close; the server rolls back an open transaction; the session stays closed |

Frames carry a client `id`; calls may overlap and answers are matched by `id`. Nothing on a session is resent.

Reconnect: when the socket drops (server restart 1001, network loss, ping timeout), calls in flight fail with the
close error and are not resent. The next call opens a new socket with a fresh credential. If a transaction was open
when the socket dropped, the server rolled it back: every call except `rollback` fails with `transaction_rolled_back`
(40000) until the caller calls `rollback`, which only clears the lost transaction. After `close()` the session stays
closed. A refused reconnect raises the same HTTP error types as `connect()`.

## Values

| Kind | Argument | Row value |
|---|---|---|
| null | `null` | `null` |
| integer | 64-bit integer | exact JSON number (read as 64-bit, never as a double) |
| real | finite double; NaN and infinity refused before sending | JSON number |
| boolean | `true`/`false`, stored as 1/0 | integer |
| text | string | string |
| blob | bytes, sent as `{"base64":"…"}` | `{"base64":"…"}`, decoded to bytes |

Named argument keys omit `:`, `@` and `$`. Statements are validated before sending: non-empty SQL, 1 to 256 per
request. A row reads by ordinal or column name (`getInt64`, `getDouble`, `getBoolean`, `getString`, `getBytes`,
`isNull`); a repeated column name resolves to its first ordinal.

## Errors

A refused statement raises `SqlError` (`TerraBaseSqlException` in .NET) with `code`, `message`, `statement`
(zero-based index) and `sqlstate`, plus the HTTP status over REST (none over the WebSocket). The request rolled back.
Codes are constants (`TerraBaseSqlErrorCodes`); the full table with SQLSTATEs and statuses is
`sdk/dotnet/contracts/terrabase/sql/error-codes.json`. `isTransient` is true for `serialization_conflict` and
`tenant_busy`.

Retries: REST resends `409 serialization_conflict` at most twice with jitter (the request rolled back entirely) and
the SDK's existing pre-execution refusals (`503` with Retry-After); nothing else. Sessions never resend.

Failures before execution keep each SDK's generic HTTP error types (401, 404, 421 wrong region with the home region,
429, 503). A refused WebSocket upgrade raises the same types.

## IDs

Primary keys are ULIDs from the SDK's ULID generator (`TerraScaleIdGenerator`, `TerraScaleIds.Validate`). Tables that
take local writes avoid `AUTOINCREMENT`.

## Multi-region write conflicts

Read the database's public, read-only `terrascale.conflicts` table with the existing `query` method. No additional SDK
method or HTTP endpoint is needed. Home-apply contract: `docs/terrabase/home-apply.md`.

```sql
SELECT * FROM terrascale.conflicts
WHERE table_name = ?
  AND julianday(created_at) >= julianday(?)
  AND julianday(created_at) < julianday(?)
ORDER BY id DESC LIMIT 50;
```

Bind the exact table name, inclusive UTC start and exclusive UTC end as positional arguments. For older records,
add `AND id < ?` with the last returned conflict ID; omit predicates for unfiltered recent records.
`created_at` is recording time, distinct from `winner_hlc` and `loser_hlc`. `row_key`, `winner_value` and `loser_value`
are JSON text; SQL NULL means no value was recorded, distinct from the JSON text `null`. Values above 4 KiB of JSON
are represented by `{"sha256":"…","bytes":12345}`. The log includes `column_name`, and both writers' `_region`,
`_node`, `_hlc`, `_transaction_id` and `_state` fields. HLC text sorts by hex milliseconds, logical counter and node ID.
`loser_state = 'superseded'` belongs to a confirmed transaction whose losing columns did not apply; `reverted` means
none of its changes applied. Provisional writes have no conflict record yet, so no records does not prove confirmation.
The home retains at most the newest 10,000 records and trims records older than 30 days when recording a conflict,
subject to replica freshness. Never reconstruct historical winners by joining against the current application row.
