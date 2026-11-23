---
name: terrascale-sdks
description: Write TerraScale SDK queries, batches and transactions in JavaScript, Python, Go, Rust or .NET. Use when choosing a TerraScale client, learning how the SDKs work, configuring credentials or regions, binding SQL parameters, paginating results or diagnosing SQL errors and uncertain writes.
---

# TerraScale SDKs

Load only the reference for the application's language. See also
`terrascale-orm-setup` for ORMs and `terrascale-migrations-and-branches` for migrations.

## Prerequisites

- An existing project, its project ULID, a database name or ULID, and an authorized
  API key from the dashboard. Reads need `sql:read`; writes and schema changes
  also need `sql:write`. Keep the key in the application's secret manager.
- Supply `TERRASCALE_API_KEY`, `TERRASCALE_PROJECT_ID` and
  `TERRASCALE_DATABASE_NAME` to the examples without printing their values.
  The database variable is read by these examples, not automatically by the SDKs.
- Check package availability before installing: current source and published
  packages differ. JavaScript's published `0.0.17` is the previous API;
  the quickstart uses source `0.0.19`. Go's documented install is `v0.0.27`.
  Python is source-only pending PyPI; .NET is a preview outside NuGet;
  Rust is unpublished and requires a local source dependency.

Sources: [getting started](https://docs.terrascale.tech/getting-started/),
[SDK reference](https://docs.terrascale.tech/guides/sdks),
[JavaScript source quickstart](https://docs.terrascale.tech/getting-started/javascript).
The methods in this skill were checked against monorepo `origin/main` commit
`da3382de73201d4fc94dd6872fd2cad5a97de12f`; source availability is not a claim
that every method is published or deployed. Recheck the installed version before
using a newer source method.

## 1. Inventory the application and choose its interface

- [ ] Identify runtime, package version, project/database, scopes and server/browser use.
- [ ] Determine whether work fits one request or must read before its next write.
- [ ] Select the interface below; use the SQLite dialect described by the public
  [SQL model](https://docs.terrascale.tech/concepts/sql).

| Interface | Choose it when | Boundary |
| --- | --- | --- |
| JavaScript / TypeScript SDK | Node.js 22/24 or browser application code | Current source supports REST and WebSocket; keep project keys on the server and use end-user tokens in browsers. |
| Python SDK | Python 3.10+ applications, sync or asyncio | Choose the matching sync/async client and close it. |
| Go SDK | Go 1.25+ services | Pass a context to every operation; project selection is on the database handle. |
| Rust SDK | An application can depend on the unpublished local source | Use the project client and `.database(name).sql()` for REST. |
| .NET SDK | .NET 10/11 applications able to consume a preview | Build source with the repository's .NET 11 toolchain; SQL methods are async. |
| SQL HTTP API | No suitable SDK, a small HTTP integration, or direct wire control | Exchange the API key for a bearer token; one request is one transaction. |
| Postgres driver | Existing `psql`, node-postgres, psycopg, pgx or Npgsql integration | PostgreSQL wire protocol is a transport; SQL still uses the SQLite dialect. |
| ORM | Existing application needs its ORM mapping layer | See also `terrascale-orm-setup`; check its documented provider/dialect first. |

Sources: [SDK reference](https://docs.terrascale.tech/guides/sdks),
[JavaScript](https://docs.terrascale.tech/getting-started/javascript),
[Python](https://docs.terrascale.tech/getting-started/python),
[Go](https://docs.terrascale.tech/getting-started/go),
[Rust](https://docs.terrascale.tech/getting-started/rust),
[C#](https://docs.terrascale.tech/getting-started/csharp),
[SQL HTTP](https://docs.terrascale.tech/guides/sql-http),
[Postgres drivers](https://docs.terrascale.tech/guides/postgres-clients).
Postgres connections use `pg.terrascale.tech:5432`, a database selector of
`project/database`, an API key as password, and verified TLS. They do not require
the application's SDK token-exchange path.

## 2. Configure one reusable client and database handle

The common shape is client → database handle → `execute`, `query` or `batch`.
Constructing a handle does not create a database. API-key clients exchange the
key at ts-auth for short-lived project access tokens and refresh them; SQL goes
to the API origin. Do not send the API key as an HTTP SQL bearer token.
Sources: [SDK reference](https://docs.terrascale.tech/guides/sdks),
[SQL HTTP credentials](https://docs.terrascale.tech/guides/sql-http).

| Language | Environment constructor | Database selection | Detailed instructions |
| --- | --- | --- | --- |
| JS/TS | `createTerraBaseClientFromEnvironment()` | `client.database(name)` | [JavaScript](references/javascript.md) |
| Python | `TerraScale()` | `client.database(name)` | [Python](references/python.md) |
| Go, current source | `v1.NewFromEnvironment()` | `client.Database(project, name)` | [Go](references/go.md) |
| Rust | `Client::from_environment()` | `client.database(name).sql()` | [Rust](references/rust.md) |
| .NET | `TerraBaseSqlClient.FromEnvironment()` | `client.Database(project, name)` | [.NET](references/dotnet.md) |

- [ ] For released Go `v0.0.27`, use `v1.New(Config{APIKey: key})`; its tag lacks
  `NewFromEnvironment`, `RegionPin` and `TSAuthBaseURL`. Those require newer source.
  Named statement helpers and uncertain-write recovery also differ; read the
  [Go release guidance](references/go.md) before implementing error handling.
- [ ] Use the public API origin `https://api.terrascale.tech` by default;
  `TERRASCALE_BASE_URL` is an optional override in all five environment clients.
- [ ] JS, Python and Rust read the project from `TERRASCALE_PROJECT_ID`.
  Go and .NET receive it on each database handle.
- [ ] Only .NET's environment constructor reads `TERRASCALE_REGION`.
  For an explicit pin use JS `region`, Python `region`, Go `Config.RegionPin`,
  Rust `.region_pin(...)`, or .NET `TerraBaseSqlClientOptions.Region`.
- [ ] Zero-configuration means no platform activation flags, region discovery
  or manual platform-secret copying. Application credentials, authorization and
  customer-selected database placement still apply.

Sources: [language quickstarts](https://docs.terrascale.tech/guides/sdks),
[regions](https://docs.terrascale.tech/concepts/regions),
[zero-configuration](https://docs.terrascale.tech/guides/zero-configuration).

When an API-key client uses a nondefault API origin or an endpoint resolver,
explicitly name its authentication service: JS `authBaseUrl`, Python `issuer`
(an origin followed by `/terrascale`), Go `Config.TSAuthBaseURL`, Rust
`.auth_base_url(...)`, .NET `AuthAddress`. Otherwise construction is refused.
Use the explicit constructor for this setup: Go, Rust and .NET environment
constructors cannot supply that extra setting. JS accepts overrides and Python
accepts `issuer=`. Origins are origins, without an API path, query, fragment or
credentials; use HTTPS except for supported loopback development origins.
See the language references and [authentication](https://docs.terrascale.tech/guides/authentication).

### First query after installing the matching source/package

JavaScript, server-side:

```js
import { createTerraBaseClientFromEnvironment } from "@terrascale/sdk";
const db = createTerraBaseClientFromEnvironment().database(process.env.TERRASCALE_DATABASE_NAME);
console.log(await db.query("SELECT ? AS value", [42]));
```

Python:

```python
import os
from terrascale import TerraScale
with TerraScale() as client:
    db = client.database(os.environ["TERRASCALE_DATABASE_NAME"])
    print(db.query("SELECT ? AS value", [42])[0].get_int64("value"))
```

Go, inside a function with `ctx` and imports `os` and `tb "terrascale.tech/sdk/v1"`:

```go
client, err := tb.New(tb.Config{APIKey: os.Getenv("TERRASCALE_API_KEY")})
if err != nil { return err }
db := client.Database(os.Getenv("TERRASCALE_PROJECT_ID"), os.Getenv("TERRASCALE_DATABASE_NAME"))
rows, err := db.Query(ctx, "SELECT ? AS value", int64(42))
if err != nil { return err }
var value int64
if err := rows[0].Scan(&value); err != nil { return err }
```

Rust, inside an async function returning `terrascale::v1::Result<()>`:

```rust
use terrascale::v1::Client;
let client = Client::from_environment()?;
let name = std::env::var("TERRASCALE_DATABASE_NAME").expect("TERRASCALE_DATABASE_NAME");
let rows = client.database(name).sql().query("SELECT ? AS value", &[42_i64.into()]).await?;
println!("{}", rows[0].get_i64("value")?);
```

.NET, with `using TerraScale.DotNet.TerraBase;` and a caller cancellation token `ct`:

```csharp
var client = TerraBaseSqlClient.FromEnvironment();
var project = Environment.GetEnvironmentVariable("TERRASCALE_PROJECT_ID") ?? throw new InvalidOperationException();
var name = Environment.GetEnvironmentVariable("TERRASCALE_DATABASE_NAME") ?? throw new InvalidOperationException();
var rows = await client.Database(project, name).QueryAsync("SELECT ? AS value", [42], ct);
Console.WriteLine(rows[0].GetInt64("value"));
```

Sources: [five quickstarts](https://docs.terrascale.tech/guides/sdks).

## 3. Bind values and choose a transaction boundary

- [ ] Use `execute` for one statement result, `query` for rows, and `batch` for
  several statements known in advance. Every REST call is one atomic transaction.
- [ ] Send 1–256 statements per batch; use statement objects for per-statement
  arguments. `BEGIN`, `COMMIT` and `ROLLBACK` are refused over REST.
- [ ] Use positional `?` values or named `:name` values with map keys `name`.
  Bind values; do not interpolate user input into SQL. Choose identifiers from
  an application allowlist rather than treating identifiers as bind parameters.
- [ ] Preserve 64-bit integers; JS uses `bigint` for large arguments and
  `ExactDecimal` for large returned integers. Booleans store as 1/0; blobs use
  the SDK's bytes type. Null and finite real values are supported.
- [ ] For read/decide/write, call the database's `connect`/`ConnectAsync` method,
  then use the session's transaction callback. It begins, runs the callback and
  commits; a callback failure rolls back and propagates the error.
- [ ] Close the session in a context manager, `finally`, defer or async disposal.
  Sessions do not automatically resend SQL, even on `serialization_conflict`.

Sources: [SQL HTTP](https://docs.terrascale.tech/guides/sql-http),
[SDK reference](https://docs.terrascale.tech/guides/sdks),
[JavaScript values](https://docs.terrascale.tech/getting-started/javascript),
[SQL model](https://docs.terrascale.tech/concepts/sql).

### Pagination and budgets

SQL rows have no SDK page cursor. Use a deterministic `ORDER BY` and a bounded
`LIMIT`, carrying the last unique key into the next query. For the example
schema, first request `SELECT id, name FROM sdk_people ORDER BY id LIMIT ?`;
later requests use
`SELECT id, name FROM sdk_people WHERE id > ? ORDER BY id LIMIT ?`.
Bind the last ID and page size, and stop on an empty page. Concurrent changes
can affect separate requests; this is not a snapshot across pages.

Keep results below 10,000 rows and 8 MiB, SQL text below 16 KiB, bodies below
1 MiB, and each transaction within 1,024 changed rows and 8 MiB file growth.
Keep interactive work short and within the documented five-second transaction
input/wait budget. Accepted execution or commit can finish later; elapsed time
alone never establishes rollback. Plan query/scan limits also apply.
Sources: [limits](https://docs.terrascale.tech/limits),
[errors and transaction timing](https://docs.terrascale.tech/guides/errors),
[SQL model](https://docs.terrascale.tech/concepts/sql).

Cancellation uses JS `AbortSignal` on REST calls and connection opening, Go
`context.Context`, .NET `CancellationToken`, Python async task cancellation and
Rust future cancellation. Python's `timeout=` and Rust's builder `.timeout(...)`
configure transport timeouts; neither changes server query or transaction limits.
A canceled/lost write response can still have committed. See the language
references and [safe retries](https://docs.terrascale.tech/guides/errors).

## 4. Verify regional behavior and write outcomes

- [ ] Treat a region pin as entry-region selection, not database placement or
  a promise that all reads are current. Reads run in the called region;
  REST reads are eventually consistent. Sessions preserve read-your-writes.
- [ ] Let the platform order writes. A write may be forwarded or committed
  locally and confirmed. Region lists are sorted and do not identify the writer.
- [ ] If raw HTTP routing control is required, consult
  [SQL HTTP](https://docs.terrascale.tech/guides/sql-http): `TerraScale-Write: home`
  selects the ordering region, and `TerraScale-Durability: home` waits for its
  application. Do not invent a shared SDK `writeRegion` or durability option.
- [ ] Generate ULID primary keys with each SDK's ID generator for tables taking
  local writes; avoid `AUTOINCREMENT` there.
- [ ] Distinguish `provisional`, `confirmed` and `reverted`. Confirmation does
  not mean every column won: greater HLC stamps win per column, and writes to
  different columns may both survive. Constraints can revert a transaction.

Sources: [regions](https://docs.terrascale.tech/concepts/regions),
[multi-region writes](https://docs.terrascale.tech/guides/multi-region-writes-and-conflicts),
[SDK IDs](https://docs.terrascale.tech/guides/sdks).

Read conflicts with ordinary `query`; no special SDK conflict method is needed:

```sql
SELECT * FROM terrascale.conflicts
WHERE table_name = ?
  AND julianday(created_at) >= julianday(?)
  AND julianday(created_at) < julianday(?)
ORDER BY id DESC LIMIT 50
```

Bind the exact table name, inclusive UTC start and exclusive UTC end. For older
pages add `AND id < ?` before `ORDER BY`. An empty log does not prove confirmation.
`winner_value` and `loser_value` contain historical JSON text; SQL NULL differs
from the text `null`. Values above 4 KiB are represented by a hash and size.
Source: [conflict log](https://docs.terrascale.tech/guides/multi-region-writes-and-conflicts).

## 5. Diagnose errors, reconcile and retry safely

Inspect typed code/status/SQLSTATE and the zero-based failing statement index.
Keep `X-Request-ID` or the error body's `requestId` for support without logging
credentials or SQL argument values. A transient flag alone is not proof that a
write is safe to repeat. Source: [errors](https://docs.terrascale.tech/guides/errors).

| Symptom or code | Action |
| --- | --- |
| `401`, `key_expired`, `key_revoked` | Supply a current authorized key/token; do not loop on invalid credentials. |
| `403 forbidden_scope`, `ip_not_allowed` | Correct scopes or the caller's allowed network. |
| `404 unknown_database` | Check project, database name/ULID and key restrictions; hidden resources also return 404. |
| `421 wrong_region` / `region_pin_mismatch` | Inspect routing hints; use a serving region or correct/remove the pin. SDKs do not automatically retry 421 elsewhere. |
| `409 serialization_conflict` / `40001` | Request rolled back; REST SDKs resend at most twice. Sessions require the caller to reconsider the whole transaction. |
| `429 tenant_busy` / `53300`, temporary `503` | Honor `Retry-After` for a safe operation within its deadline. Do not replay an ambiguous write. |
| `quota_exceeded`, `storage_quota`, `operation_allowance`, `read_fair_use` | No immediate retry; free quota, upgrade or wait for the allowance period as applicable. |
| `query_canceled` / `57014` | Narrow/index the query or split the batch; inspect whether the message names work or execution time. |
| `too_large` / `54000`, `payload_too_large` | Reduce statement/body/result/transaction size; paginate reads. |
| `primary_key_violation`, `unique_violation`, foreign-key/check failures | Correct the data or explicit application conflict decision. |
| `transaction_aborted` / `25P02` | Roll back before starting the work again; do not continue issuing statements in the aborted transaction. |
| `transaction_rolled_back` / `40000` | Reconsider the whole transaction; distinguish a confirmed rollback from an uncertain committing reply. |
| `write_outcome_unknown` / `08007`, commit-uncertain error, lost write reply | Read back expected state through a new connection before deciding to repeat. Never assume rollback or blindly resend. |
| `replica_not_ready`, `move_in_progress` | A placement operation is pending; repeat the same placement only as documented, bounded, rather than issuing a different change. |

The table follows [errors and safe retries](https://docs.terrascale.tech/guides/errors)
and [regional placement](https://docs.terrascale.tech/concepts/regions).
REST has no caller-supplied idempotency-key guarantee; do not invent one.
Use an application operation ID or conditional write and reconciliation when
an outcome is uncertain. Source: [uncertain writes](https://docs.terrascale.tech/guides/errors#uncertain-writes).

Before a WebSocket exists, 429/503 with parseable `Retry-After` can be retried:
no SQL was sent. Current source permits three overload attempts with waits capped
at 30 seconds within the connection deadline. JS needs a compatible WebSocket
implementation such as `ws` to expose upgrade refusals. Current source permanently
fails a session after an unanswered committing request. Released Go `v0.0.27`
does not guarantee this: detect both uncertain codes, explicitly close/discard
the session and reconcile through a new connection. A later local rollback
response must not override the original uncertainty. See
[errors](https://docs.terrascale.tech/guides/errors) and the
[Go release guidance](references/go.md) for the source-checked differences.

## 6. Verify the integration and change over

- [ ] Run the reference's first query with the selected package/version and database.
- [ ] Verify binding, atomicity, cleanup and typed errors in the application's workflow.
- [ ] Check pagination with concurrent writes and regional consistency before cutover.
- [ ] Retain the previous application deployment for rollback; reconcile unknown writes.

Sources: [SQL HTTP](https://docs.terrascale.tech/guides/sql-http),
[regions](https://docs.terrascale.tech/concepts/regions), [safe retries](https://docs.terrascale.tech/guides/errors).
