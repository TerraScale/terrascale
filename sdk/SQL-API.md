# SQL SDK API shape

Every TerraScale SDK exposes the same SQL surface with the same names, adjusted only to the language's casing and
async conventions. The .NET SDK (`sdk/dotnet`, `TerraBaseSqlClient`) is the reference. Server contracts:
`docs/terrabase/sql/native-sql.md` and `docs/terrabase/sql/websocket.md`; recorded fixtures:
`sdk/dotnet/contracts/terrabase/sql/`.

## Client

| Concept | .NET | Notes |
|---|---|---|
| Client | `new TerraBaseSqlClient(http, options)` | options: `BaseAddress` or `Region` (default origin `https://api.terrascale.tech`), `ApiKey` or `AccessTokenProvider`, and `AuthAddress`, the ts-auth origin that exchanges an `ApiKey` |
| Client from the environment | `TerraBaseSqlClient.FromEnvironment()` | `TERRASCALE_API_KEY`; optional `TERRASCALE_BASE_URL`, default `https://api.terrascale.tech`, and `TERRASCALE_REGION`, which pins every request to that region |
| Database | `client.Database(projectId, database)` | name or ULID; no request |

| .NET | JS/TS | Go | Python | Rust |
|---|---|---|---|---|
| `TerraBaseSqlClient.FromEnvironment()` | `createTerraBaseClientFromEnvironment()` | `v1.NewFromEnvironment()` | `TerraScale()` | `Client::from_environment()` |

JS/TS, Python and Rust also read `TERRASCALE_PROJECT_ID`; .NET and Go take the project per database. Only .NET reads
`TERRASCALE_REGION`. Without `TERRASCALE_BASE_URL` every SDK sends to `https://api.terrascale.tech`.

A client built from an API key (`ApiKey`, `apiKey`, `Config.APIKey`, `api_key`, `for_project_api_key`) sends the key to
`https://auth.terrascale.tech` unless the caller names another ts-auth. With a TerraBase origin other than
`https://api.terrascale.tech`, or an endpoint resolver, every SDK refuses to build such a client, before any request,
unless the caller names the ts-auth that exchanges the key:

| .NET | JS/TS | Go | Python | Rust |
|---|---|---|---|---|
| `AuthAddress` | `authBaseUrl` | `Config.TSAuthBaseURL` | `issuer=` (the issuer, `<origin>/terrascale`) | `.auth_base_url(origin)` |

The environment clients take no such variable: with another `TERRASCALE_BASE_URL`, pass the option (JS/TS overrides,
Python `issuer=`); .NET, Go and Rust refuse.

A token source built separately (Go `tsauth.NewAPIKeySource`, Python `APIKeyTokenSource`) sends the key to the issuer
it is given, `https://auth.terrascale.tech/terrascale` by default, whatever origin the client that uses it sends to.

Every SDK checks the TerraBase origin (`BaseAddress`, `baseUrl`, `Config.BaseURL`, `base_url`, `endpoint`) and the
ts-auth origin (the options above, .NET `TerraScaleAuthClientOptions.BaseAddress`, JS/TS `apiBaseUrl`, Go
`Config.TSAuthBaseURL` and `tsauth` `Issuer`, Rust `ClientConfig::auth_base_url`) with one rule when the client is built,
before any request. The rule reads the string as the caller wrote it, before a URL parser can repair it:

| Part | Rule |
|---|---|
| Form | Exactly `scheme://host` or `scheme://host:port`, with an optional single trailing `/`, at most 2,048 characters; a longer string is refused before any other check. Anything else is refused: user info (also an empty `@`), a path, a query or a fragment (also an empty `?` or `#`), a backslash, a percent sign, whitespace, a control or non-ASCII character, and a missing or doubled `/` after the scheme. |
| Scheme | `https` in any ASCII case. `http` only for `localhost` (any ASCII case), an IPv4 address in `127.0.0.0/8` and `[::1]` (the IPv6 loopback address in any spelling, such as `[0:0:0:0:0:0:0:1]`); not for an IPv4-mapped IPv6 address or any other name. |
| Host | A name of at most 253 characters: ASCII letters, digits and hyphens in dot-separated labels of 1 to 63 characters, with no trailing dot; a label neither begins nor ends with a hyphen and does not begin with `xn--`, so internationalised names are refused: TerraScale's hosts are ASCII, and the five HTTP libraries read such names five different ways. Or an IPv4 address written as four decimal octets without leading zeros; or a bracketed IPv6 address of hex groups, without a zone id or a dotted IPv4 part. A name whose last label is all digits, or `0x` and hex digits, is an IPv4 address and must be written as one. The name `loopback` (which `System.Uri` reads as `localhost`) and the unspecified addresses `0.0.0.0` and `[::]` in any spelling, such as `[0:0:0:0:0:0:0:0]`, and `0.0.0.0` in its IPv4-mapped form, such as `[::ffff:0:0]`, are refused for both schemes. The SDK uses the host in lower case. |
| Port | Decimal 1 to 65535 without leading zeros. `:443` (`:80` for `http`) is the same origin as no port; any other port is kept. |
| Sent as checked | The library that sends the request (Go `net/url`, the WHATWG URL parser in JS/TS, Python httpx, Rust `url`, .NET `System.Uri`) must parse the checked origin to the same host and port; otherwise the origin is refused, also when that parser raises its own error (Python compares the `host` httpx's request reads). Where that parser re-spells an IPv6 address, as the WHATWG parser, `url` and `System.Uri` do, the host is compared as an address. .NET refuses a `TERRASCALE_BASE_URL`, or an `Endpoint` or `AuthEndpoint` of the EF Core connection string, that `System.Uri` cannot parse with the same `ArgumentException`. A .NET option is a `Uri`, so the caller's `new Uri(...)` parses the string first and can refuse it with its own `UriFormatException` before the SDK checks the `Uri`'s `OriginalString`. |
| Proxies | The SDKs' own clients reach a plain-http origin, always a loopback host, directly, its SQL WebSocket included, never through `HTTP_PROXY` or `ALL_PROXY`: the httpx clients Python creates send `http://` requests through a direct transport and open a `ws://` SQL session with websockets' `proxy=None`, .NET's `TerraBaseHttp.CreateHandler()` and the handler of `AddTerraScaleAuthClient` bypass `HttpClient.DefaultProxy` for an http or ws request to a loopback host, Go's `ProxyFromEnvironment` skips loopback hosts, Rust reads no proxy variable, and JS/TS sends through the runtime's `fetch`, which Node runs without a proxy. Two setups send such a request, credential included, through the environment's proxy unless the loopback host is in `NO_PROXY`: a JS/TS runtime whose `fetch` reads the proxy variables, such as Node with `NODE_USE_ENV_PROXY=1`, and a .NET client over a handler the caller creates, `new HttpClient()` included. For an https origin Go, Python and .NET use the environment's proxy through a CONNECT tunnel, so the credential stays inside TLS. |
| TerraScale's own hosts | Under `terrascale.tech` the TerraBase origin is `api.terrascale.tech` or `<cell>.api.terrascale.tech`, and the ts-auth origin is `auth.terrascale.tech` or `<cell>.auth.terrascale.tech`, with one label for the cell (`gru`, `sjc`, `fra` today); any other `terrascale.tech` host is refused for both. An API key goes to `https://auth.terrascale.tech` only when the TerraBase origin is `https://api.terrascale.tech` in any case, with or without `:443` and `/`. |
| Issuer | An issuer (Go `tsauth` `Issuer`, Python `issuer=`) is a ts-auth origin without the trailing `/`, then `/` and one tenant, `terrascale` or a project id (a canonical ULID), unescaped, such as `https://auth.terrascale.tech/terrascale`. |

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
| `createScopedKey({ project?, database, access, ttlSeconds? })` | `POST https://auth.terrascale.tech/v1/terrascale/api-keys/self/scoped-keys`, or the same path on the ts-auth the client names, `Authorization: Basic base64(keyId:key)` of the client's `ApiKey`, `{"project","database","access","ttl_seconds"}` | `ScopedKey` (201) |

`ManagedDatabase` is the existing database body (`id`, `projectId`, `name`, `branchedFrom`, `createdAt`, `region`,
`regions`, …) plus `expiresAt` (RFC 3339, or null without a TTL).

- `ttlSeconds` is 60 to 31,536,000 (365 days). The database, its backups and its data keys are deleted (crypto-shredded)
  within seconds after `expiresAt`; deletion is the same as `deleteDatabase`. A TTL database counts against the plan's
  database limit and is billed like any other until it is deleted. A fork does not inherit the source's TTL.
- `setDatabaseTtl` and `clearDatabaseTtl` return `409 move_in_progress` while the database's writer is moving; send the request again after it settles.
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

Placement selects one set of regions; TerraBase internally keeps a writer and synchronizes the others. It does not
add independent multi-writer consensus or change SQL write routing. Server contract: `docs/terrabase/placement.md`.
Routes need `databases:write`; reading status needs `databases:read`.

| Method | Request | Returns |
|---|---|---|
| `createDatabase(project, name, { regions?, allRegions?, ttlSeconds? })` | `POST /v1/projects/{project}/databases` with `{"name":"main","regions":["gru","fra"]}` | `ManagedDatabase` (201) |
| `setDatabasePlacement(project, database, { regions?, allRegions? })` | `PATCH …/databases/{database}` with `{"regions":["fra"]}` or `{"allRegions":true}` | `ManagedDatabase` (200) |

`regions` is a nonempty set of distinct serving region ids, at most nine including the writer and any intermediate
superset (eight replicas besides the writer). Unknown, non-serving, duplicate, empty or oversized sets return
`400 invalid_request`. Send `regions` separately from `homeRegion`, `replicaRegions` and `allRegions`;
send a rename separately. Omitted options are not sent. `allRegions: true` without `regions` includes every serving
region, including ones that join later. Explicit `regions` turns that mode off.

On create the first requested region is the initial writer. Response `regions` are sorted by id; their order carries
no meaning and does not identify the writer. Responses carry integer `placementVersion`: membership or writer changes
raise it; an unchanged region set retains it. Transitional response fields are accepted and ignored, and their future
absence is accepted too. During the transition an old-field PATCH still bumps the version when repeated,
and returns `409 move_in_progress` while a move is pending so membership cannot consume the move's reserved
version or diverge from the placement sent to its target.

If an update excludes the current writer, missing requested regions are added and stay added. A call that adds regions
returns `503 replica_not_ready`, retaining the old writer, existing members and requested additions. Nothing is unstaged
on any refusal. When no regions need adding, including on the first call, TerraBase reads pull observations and tries
one candidate: the first requested region in region-id order that has replicated this database at least once.
The move brings it up to date or refuses with `503 replica_not_ready`. Promotion then removes every unselected region
and returns 200. Repeat the identical request after replication (about 30 seconds for a newly added region).
No ready candidate or a repeat during the same regions move returns `503 replica_not_ready`; a different move
returns `409 move_in_progress`. SDKs preserve these distinct codes and do not automatically retry either refusal.
The intermediate superset must also fit the nine-member limit before any membership is written; remove other
non-writers first to make room. Unresolved moves settle through the existing move mechanism.

A direct request through a region no longer selected returns `421 wrong_region`, with routing hints in
`X-TerraBase-Database-Home` and `X-TerraBase-Database-Regions`; the global API edge replays it.
Each SDK preserves the code and available diagnostics; callers using a pinned endpoint choose a current region.
`GET …/databases/{database}/regions` returns `databaseId`, `observedAt` and `regions` entries with `region`,
nullable `role` (`"writer"` on the writer), `revision`, `lastPulledAt`, `lagMs` and `lastVerified`.
Pull observations describe completed served pulls, not application or checksum acknowledgements.

| .NET | JS/TS | Go | Python | Rust |
|---|---|---|---|---|
| `client.SetDatabasePlacementAsync` | `client.setDatabasePlacement` | `client.SetDatabasePlacement` | `client.set_database_placement` | `client.set_database_placement` |

## REST: one request, one transaction

`POST /v1/projects/{project}/databases/{database}/sql/execute`

| Method | Returns | Notes |
|---|---|---|
| `execute(sql, args?)` / `execute(sql, named)` / `execute(statement)` | `StatementResult` | one statement |
| `query(sql, args?)` / `query(sql, named)` | `Row[]` | rows of one statement |
| `query<T>(sql, args, mapper)` | `T[]` | typed rows; the row becomes an object keyed by column name, blobs as base64 strings |
| `batch(statements)` | `StatementResult[]` | 1 to 256 statements, all or nothing, one result per statement |

`execute(statement)` runs one statement value, the shape `batch` takes, on the database, on a session and inside a
transaction:

| .NET | JS/TS | Go | Python | Rust |
|---|---|---|---|---|
| `ExecuteAsync(new TerraBaseSqlStatement(sql, args))` | `execute({ sql, args })` / `execute({ sql, named })` | `ExecuteStatement(ctx, v1.Statement{SQL: sql, Args: args})` | `execute(Statement(sql, args))` | `execute_statement(SqlStatement::new(sql).args(args))` |

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

Reconnect: when the socket drops (server restart 1001, regional handoff 1012, network loss, ping timeout), calls in
flight fail with the close error and are not resent. The next call opens a new socket with a fresh credential (except
JavaScript: open a new session). If a transaction was open when the socket dropped, the server rolled it back: every
call except `rollback` fails with `transaction_rolled_back` (40000) until the caller calls `rollback`, which only clears
the lost transaction. After `close()` the session stays closed. A refused reconnect raises the same HTTP error types as
`connect()`.

### Keep-alive and closes

| Event | What happens |
|---|---|
| An idle socket | TerraBase pings every 20 seconds and drops a client that does not answer a ping within 20 seconds. |
| The client takes no data for 120 seconds | The edge ends the socket: an HTTP/1.1 connection closes, an HTTP/2 stream gets RST_STREAM. |
| An upgrade without `Authorization` sends no auth frame within 5 seconds | TerraBase closes the socket with 1008. |
| The region's writer starts handing off to another node (regional handoff), socket through the edge (`api.terrascale.tech`) | The edge ends every socket to the region when its own hold starts, before TerraBase's fence: the client sees an abnormal close (1006), and a call in flight has an unknown outcome, so it may have committed. |
| The same handoff, socket straight to TerraBase | TerraBase answers every frame it admitted before the handoff, then closes the socket with 1012 (service restart); a call still waiting for its answer when the 1012 arrives did not run. The handoff does not wait for an answer that is still being sent: a client still receiving one when the old writer stops sees an abnormal close (1006), and that call has an unknown outcome, so it may have committed. While the handoff holds the region's ingress, and on a node that was drained for a handoff (the old writer after the hold, or a follower drained for retirement) until that node stops, an upgrade answers 503 with `Retry-After: 1`. Reconnect: the new socket reaches the region's writer. |
| TerraBase closes the socket with 1012, or with 1001 at a restart | TerraBase still sends every answer queued before the close, runs no frame that arrives later and waits for the client to answer the close. A 1001 does not wait for the call in flight: that call may have run, and its answer is not sent. A client that keeps sending after the 1012 instead of answering it gets no answer to those frames and stays connected until the node stops; close the socket when you read a 1012. A client that sends nothing and never answers is dropped by the ping timeout, 40 to 50 seconds after its last frame; at a restart every connection still open 2 seconds after shutdown starts is dropped. |
| TerraBase closes the socket for a client fault (1003, 1008, 1009, 1011) and the client does not answer within 2 seconds | TerraBase drops the connection. |

What each SDK does on a 1012 today:

| SDK | Calls in flight | The next call | Code |
|---|---|---|---|
| .NET | fail with `WebSocketException` (`ConnectionClosedPrematurely`, the message names 1012) | opens a new socket | `sdk/dotnet/src/TerraScale.DotNet/TerraBase/Sql/TerraBaseSqlSession.cs:274-277, 296-301, 184-198` |
| JS/TS | reject with `SqlSocketClosedError` (`closeCode` 1012) | rejects with the same error; the caller opens a new session with `database.connect()` | `sdk/js/src/v1/sql-socket.js:64, 199, 237-243` |
| Go | fail with `terrabase: SQL session socket closed: …` wrapping the close error | redials | `sdk/go/v1/sql_session.go:292-307, 208-231` |
| Python | raise `TerraScaleError`, or `CommitUncertainError` for a commit or an execute outside a transaction | reopens | `sdk/python/src/terrascale/session.py:216-240, 89-96` (async: `389-404`) |
| Rust | fail with `Error::Transport` (`SQL session closed: TerraBase closed the socket (1012: …)`) | opens a new socket | `sdk/rust/src/v1/sql_session.rs:443-453, 461-477, 382-417` |

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
the SDK's existing pre-execution refusals (`503` with Retry-After); nothing else. Sessions never resend
statements. Before a SQL session exists, a refused WebSocket upgrade with HTTP 429 or 503 and a parseable
`Retry-After` (delay-seconds or HTTP-date) is safe to retry: no statement has been sent. All five SDKs make
at most three overload attempts (the first plus two retries), waiting the advertised delay capped at 30 seconds
between attempts. A 401 credential refresh does not consume an overload attempt. Missing or invalid
`Retry-After` means no retry. If the capped wait cannot fit the remaining connection deadline, or the attempts
are exhausted, the SDK returns the existing HTTP refusal immediately with its uncapped `retryAfter`
(`RetryAfter` / `retry_after`) delay. Cancellation interrupts the wait; asyncio task cancellation and Rust
future cancellation also stop connection work. Browser WebSockets expose neither the upgrade status nor headers,
so browsers cannot retry refused upgrades. In Node, upgrade retries require a WebSocket implementation that
exposes the HTTP response, such as `ws` through its `unexpected-response` event.
A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

Failures before execution keep each SDK's generic HTTP error types (401, 404, 421 wrong_region with routing hints,
429, 503). A refused WebSocket upgrade raises the same types.

## IDs

Primary keys are ULIDs from the SDK's ULID generator. Tables that take local writes avoid `AUTOINCREMENT`. A valid ID is
a plain 26-character uppercase Crockford ULID whose first character is 0 to 7.

| | .NET | JS/TS | Go | Python | Rust |
|---|---|---|---|---|---|
| New ID | `new TerraScaleIdGenerator().NewId()` | `generateId()` | `v1.NewID()` | `new_id()` | `new_id()` |
| Validate | `TerraScaleIds.IsValid(id)`, `TerraScaleIds.Validate(id)` | `isValidId(id)` | `v1.ValidateID(id)` | `is_valid_id(id)` | `is_valid_id(id)` |

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
