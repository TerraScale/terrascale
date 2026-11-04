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

## REST: one request, one transaction

`POST /v1/projects/{project}/databases/{database}/sql/execute`

| Method | Returns | Notes |
|---|---|---|
| `execute(sql, args?)` / `execute(sql, named)` / `execute(statement)` | `StatementResult` | one statement |
| `query(sql, args?)` / `query(sql, named)` | `Row[]` | rows of one statement |
| `query<T>(sql, args, mapper)` | `T[]` | typed rows; the row becomes an object keyed by column name, blobs as base64 strings |
| `batch(statements)` | `StatementResult[]` | 1 to 256 statements, all or nothing, one result per statement |

## WebSocket: interactive transactions

Ships once TerraBase serves the E3 protocol (`docs/terrabase/sql/websocket.md`); the .NET session is ready behind it.

`GET /v1/projects/{project}/databases/{database}/sql/ws`, subprotocol `terrascale.sql.v1`.

| Method | Notes |
|---|---|
| `database.connect()` | returns a `Session` the caller closes |
| `session.execute` / `query` / `query<T>` / `batch` | same signatures as REST; one transaction each outside a transaction |
| `session.begin()` / `commit()` / `rollback()` | frames `begin`, `commit`, `rollback`; a transaction lasts at most 5 seconds |
| `session.transaction(fn)` | begin, run `fn(session)`, commit; roll back and rethrow when `fn` throws |
| `session.inTransaction` | the last answer's `transaction` flag |
| `session.close()` | normal close; the server rolls back an open transaction |

Frames carry a client `id`; calls may overlap and answers are matched by `id`. Nothing on a session is resent.

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
