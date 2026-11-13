# TerraScale Go SDK (`terrascale.tech/sdk`)

Package `v1` is the TerraBase client for API keys and project JWTs: databases, SQL over REST
and WebSocket, backups, file buckets, functions, usage and analytics. `tsauth` exchanges
customer API keys and user tokens for project access tokens. `tsauth.VerifyWebhook`
verifies a Standard Webhooks `v1` delivery of ts-auth end-user lifecycle events and
returns the parsed event or a `*tsauth.WebhookError`. The root package
holds hosted TSAuth clients, pure SQL builders and the PostgreSQL client.

Platform entity IDs are canonical uppercase, 26-character Crockford ULIDs. Use
`sdk.NewID()` (or `v1.NewID()`) to generate one and `ValidateID` to validate it.
Generation is concurrency-safe and monotonic within a millisecond. Entity
IDs reject UUIDs, prefixes and lowercase forms. OAuth client names, database
names, region/service catalog names and credentials remain protocol names or
opaque tokens, not entity IDs.

SQL and PostgreSQL primary keys are user-chosen values, including integer, text
and composite keys; they do not need to be ULIDs. Tables that take local writes
should use ULID keys from `v1.NewID()` and avoid `AUTOINCREMENT`.

## Source and module availability

Install the module with `go get terrascale.tech/sdk@v0.0.27`; the tag carries the
current SQL API (`Database.Execute`, `Query`, `Batch` and the WebSocket session).
Follow the [CLI installation instructions](docs/cli-releases.md) for the released
executable. The historical `history/ts-go/*` releases predate the SQL-only API;
use them only with that revision's contracts. Releases remain `0.0.X`.

## Zero-configuration operating contract

The SDK has no regional discovery or failover. Auth calls go to the default
origin `https://auth.terrascale.tech` unless the caller names another ts-auth
(`Config.TSAuthBaseURL`, or `Issuer` in `tsauth`), the default API origin is
`https://api.terrascale.tech`, and ts-orch on OCI handles routing. No region
activation, endpoint pinning or manual secret copying belongs in setup.
Customer tenant/project scope and authorized access credentials remain required.
The examples describe SDK boundaries; deployed issuer trust still requires
qualification. See the [zero-configuration contract](https://docs.terrascale.tech/guides/zero-configuration).

`Client.ListRegions(ctx)` reads the anonymous [serving-region catalog](contracts/regions.md)
for database placement. It sends one request, accepts future valid region IDs, and
adds no routing, cache or background discovery. No credentials are required.

## Base URL rules

`v1.Config.BaseURL` and `TSAuthBaseURL`, the root `Config`'s `APIBaseURL` and `TSAuthBaseURL`, and the `tsauth`
`Issuer` follow the origin rule of [`sdk/SQL-API.md`](../SQL-API.md#client). The constructor returns an error, before
any request, for a base URL with user info or a path, a string of more than 2,048 characters, a name of more than 253
characters or with a label of more than 63, a label that begins or ends with a hyphen or begins with `xn--`, the name
`loopback`, and the unspecified addresses `0.0.0.0`, `[::]` and `[::ffff:0:0]` in any spelling. A call fails the same
way, before sending, when a `Resolver` returns such an endpoint.

## TerraBase v1 client (`terrascale.tech/sdk/v1`)

Package `v1` follows the implemented C# API. A database is addressed by its
project ULID and database name. The project access token must reach that exact
project and, when narrowed, that database. TerraBase verifies the platform
issuer `https://auth.terrascale.tech/terrascale`, audience
`https://api.terrascale.tech`, lifetime, signature and required scopes.

```go
import tb "terrascale.tech/sdk/v1"
import "terrascale.tech/sdk/tsauth"

tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: apiKey, Project: projectID}) // <ULID>_<secret>
c, err := tb.New(tb.Config{TokenSource: tokens}) // fixed global API origin
db := c.Database(projectID, "main")
_, err = db.Create(ctx)                     // POST; existing name returns name_taken
m, err := db.Info(ctx)                      // metadata by name or ULID
err = db.Delete(ctx)                        // DELETE; needs databases:write
```

Every call resolves its endpoint through `tb.Resolver`. `BaseURL` wraps a
`tb.StaticResolver`; alternatively, set `Config.Resolver` to a concurrent-safe
resolver implementing `Resolve(context.Context) (string, error)`. Omit both
for `https://api.terrascale.tech`. Resolved URLs are validated before credentials are
sent, resolution errors stop the call, and retries keep the original endpoint.
The same transport configuration applies to `NewManagement`. `Token` and
`TokenSource` are mutually exclusive. A source with `Invalidate(string)` is
refreshed once after a 401, with the same request and grant; a second denial
stops the call. Explicit tokens and 403/404 responses never use a fallback.
`Config.RegionPin` sends `X-TerraScale-Region-Pin`; an empty value sends no header.

Errors are `*tb.APIError{Status, Code, Message, RetryAfter, Body, RequestID}`,
and every error code is also an error value for `errors.Is`, for example
`tb.ErrNotFound`, `tb.ErrUnknownDatabase` or `tb.ErrNameTaken`. `Body`
preserves the complete error response, including malformed JSON; `RequestID`
preserves an `X-Request-ID` response header when supplied, and `Error()` ends
with `[request <id>]` so logged errors can be traced. Truncated or oversized
error responses retain bounded bytes and an unwrap-able read cause. Successful
typed responses reject empty, null and malformed envelopes.

Retries: TerraBase does not deduplicate retries and rejects `Idempotency-Key`,
so the client never sends one. Reads are retried after 429 `rate_limited` and
`tenant_busy` (an admission refusal) with `Retry-After`, and after the transient
rejections (`capacity`, `storage_unavailable`, `dependencies_unavailable`) with
backoff. Transport errors and gateway `502`/`504` answers are retried only for reads.
`commit_uncertain` is retried only for replay-safe reads; `write_outcome_unknown` is never retried. Mutations return uncertain outcomes without a replay:
use `errors.Is(err, tb.ErrCommitUncertain)` and re-read the state before deciding
what to do. `500 internal_error` is never retried. Requests have a fixed
30-second total budget and 64 MiB response cap. A server cooldown is never
shortened: `RetryBudgetError` retains the typed API error and full cooldown when
it cannot fit the remaining deadline. Earlier caller cancellation wins.

### Databases for agents

Use `Config.APIKey` to create a database per agent, session or test run and mint
a key that reaches only its database ULID. The client exchanges API keys at
`https://auth.terrascale.tech` and caches project tokens for REST and SQL WebSocket
calls. With a `BaseURL` other than `https://api.terrascale.tech`, or a `Resolver`,
set `TSAuthBaseURL` to the bare ts-auth origin that exchanges the key; without it
`New` returns an error before any request instead of sending the key to
`https://auth.terrascale.tech`. `APIKey`, `Token` and `TokenSource` are mutually exclusive.

```go
c, err := tb.New(tb.Config{APIKey: apiKey})
ttl := int64(3600)
db, err := c.CreateDatabase(ctx, projectID, "agent-run", tb.CreateDatabaseOptions{TTLSeconds: &ttl})
fork, err := c.ForkDatabase(ctx, projectID, db.ID, "agent-fork", tb.ForkDatabaseOptions{})
key, err := c.CreateScopedKey(ctx, tb.ScopedKeyRequest{
    Project: projectID, Database: fork.ID, Access: tb.ScopedKeyReadWrite, TTLSeconds: &ttl,
})
agent, err := tb.New(tb.Config{APIKey: key.Key})
rows, err := agent.Database(projectID, fork.ID).Query(ctx, "SELECT 1")
```

`ForkDatabaseOptions.PointInTime` accepts `*time.Time`; nil copies the source now.
Forks do not inherit TTLs. `SetDatabaseTTL(ctx, project, database, seconds)` and
`ClearDatabaseTTL(ctx, project, database)` return `ManagedDatabase`, including
nullable `ExpiresAt`; `DeleteDatabase(ctx, project, database)` deletes it.
Database TTLs are 60–31,536,000 seconds. Expiry deletes data, backups and data
keys; the database counts against limits and billing until deleted.

Scoped keys use `ScopedKeyRead` or `ScopedKeyReadWrite`, default to 3600 seconds,
and accept 60–2,592,000 seconds. They never outlive the parent; revoking or rotating
the parent revokes them. `Project` is required for organization keys. The key
is returned once and cannot manage databases or mint more keys. Scoped key calls
preserve `*tb.APIError`, including auth refusal codes and `RetryAfter`, and send
once. [Contract fixtures](contracts/terrabase/agent/) qualify the HTTP boundary;
live server behavior remains unqualified.

### Placement

Every region that holds a database is equal. `CreateDatabaseOptions` accepts
`Regions` (at least one region id, no duplicates) and `AllRegions`; unset options
are not sent. TerraBase picks which region writes and moves that on its own when
the set changes. `Database.Regions` reads each region's pull progress; an entry's
`Role` is `writer` for the region that commits writes.

```go
db, err := c.CreateDatabase(ctx, projectID, "orders", tb.CreateDatabaseOptions{Regions: []string{"gru", "sjc"}})
db, err = c.SetDatabasePlacement(ctx, projectID, db.ID, tb.DatabasePlacement{Regions: []string{"sjc", "fra"}})
```

`SetDatabasePlacement` needs exactly one of `Regions` (at least one region) or
`AllRegions`. `ManagedDatabase` carries `Regions` (every region that holds the
database, sorted by region id; the order has no meaning) and `AllRegions`.
[Contract fixtures](contracts/terrabase/placement/)
qualify the HTTP boundary. All-regions placement (`AllRegions`) holds the database
in every serving region, including regions added later (`ts db placement ... -all-regions`).

A region can accept a local SQL REST write and TerraBase applies it once. The default `TerraScale-Durability` is `local`. The server forwards a timed-out local publish with
`TerraBase-Publish-Id` set to the same transaction id, and applies that write once.
A push stream that returns NATS error 10074 is not retried, because the stream wants more copies than the cluster has.
REST reads can use a ready local region. Copies are eventually
consistent. REST, psql and SQL WebSocket can read a ready local copy. Postgres and WebSocket sessions
preserve read-your-writes; use the writer for REST reads that must see a previous write. All-regions
placement (`-all-regions` on `ts db create` and `ts db placement`) holds the database in every
serving region; otherwise set an explicit region list.

The [published pricing catalog](https://terrascale.tech/pricing.json) meters database writes and storage; selecting additional regions does not multiply storage billing. Deleting a database destroys its backup data
keys, making retained encrypted backups unreadable; physical cleanup is separate. Forks remain independent.

### File buckets

`c.CreateBucket(ctx, project, name, rules)`, `c.ListBuckets(ctx, project, page)` and
`c.DeleteBucket(ctx, project, bucketID)` manage a project's file buckets with the project
access token (`sql:read` lists, `sql:write` creates and deletes). `FileAccessRules` carries the
end-user policy; missing grants deny access. Lists page in ID order with `PageOptions.After`
only.

### File objects

`c.PutObject(ctx, project, bucketID, key, body, contentType)`, `c.GetObject(ctx, project,
bucketID, key)`, `c.ListObjects(ctx, project, bucketID, page)` and `c.DeleteObject(ctx, project,
bucketID, key)` address a bucket's immutable files by key (the file name) with the project access
token (`sql:read` reads and lists, `sql:write` uploads and deletes). `PutObject` streams the body
through a disk spill so a whole object is never held in memory, and follows the producer's
reservation flow: create the upload, PUT the exact bytes with every returned header to the
short-lived capability URL, then complete. `GetObject` returns the raw response body; the caller
closes it. `c.SignURL(ctx, project, bucketID, fileID)` issues a 60-second download capability for
a file ID, like the JS `downloadUrl`. Files are immutable; size is 1-100 MiB per object.

### Relational SQL

The SQL API follows the shared shape in [`sdk/SQL-API.md`](../SQL-API.md) and the
[REST contract](contracts/terrabase/sql-execute.md). Every call is one
`POST /v1/projects/{project}/databases/{database}/sql/execute` request and one
transaction, all or nothing.

```go
tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: os.Getenv("TERRASCALE_API_KEY"), Project: projectID})
c, err := v1.New(v1.Config{TokenSource: tokens})
db := c.Database(projectID, "main")

_, err = db.Execute(ctx, "CREATE TABLE IF NOT EXISTS people (id TEXT PRIMARY KEY, name TEXT, avatar BLOB)")
_, err = db.Execute(ctx, "INSERT INTO people VALUES (?, ?, ?)", v1.NewID(), "Ada", []byte{1, 2, 3})
_, err = db.ExecuteStatement(ctx, v1.Statement{SQL: "INSERT INTO people (id, name) VALUES (?, ?)", Args: []any{v1.NewID(), "Grace"}})
_, err = db.Execute(ctx, "UPDATE people SET name = :name WHERE name = :old", sql.Named("name", "Ada L."), sql.Named("old", "Ada"))

rows, err := db.Query(ctx, "SELECT id, name FROM people")
for _, row := range rows {
	var id, name string
	err = row.Scan(&id, &name)
}

type person struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}
people, err := v1.QueryAs[person](ctx, db, "SELECT id, name FROM people WHERE name = ?", "Ada")

results, err := db.Batch(ctx, []v1.Statement{
	{SQL: "UPDATE people SET name = ? WHERE id = ?", Args: []any{"Grace", id}},
	{SQL: "SELECT count(*) AS n FROM people"},
})

var sqlErr *v1.SQLError
if errors.As(err, &sqlErr) && errors.Is(err, v1.ErrUniqueViolation) {
	// sqlErr.Statement (zero-based), sqlErr.SQLState ("23505"), sqlErr.Status
}
```

- `ExecuteStatement` runs one `v1.Statement` value (`SQL`, `Args` or `Named`), on a
  `*Database` and on a `*SQLSession`, also inside `Transaction`.
- Arguments: `nil`, booleans (stored as 1/0), Go integers, finite floats,
  strings, `[]byte` (sent as `{"base64":"…"}`), `json.Number` and any
  `driver.Valuer`. `sql.Named` binds `:name`, `@name` or `$name`. SQLite numbers a bare `?` after
  a named parameter past that parameter's slot, so use one style per statement. A batch holds
  1 to 256 statements in at most 1 MiB; NaN, infinity and empty SQL are refused
  before sending.
- Rows: `Scan` by ordinal like `database/sql` (including `sql.Null*` scanners),
  or read by column name with `Int64`, `Float64`, `Bool`, `String`, `Bytes`,
  `IsNull` and `Value`; a repeated column name resolves to its first ordinal.
  Cells are `nil`, `int64` (exact), `float64`, `string` or `[]byte`. `QueryAs[T]`
  maps each row as a JSON object keyed by column name; blobs map to `[]byte`.
- Errors: a refused statement returns `*v1.SQLError` with `Code`, `Message`,
  `Statement`, `SQLState` and `Status`; it wraps the `*APIError`, so
  `errors.Is(err, v1.ErrSerializationConflict)` and every other code constant
  match. Codes are in [error-codes.json](contracts/terrabase/sql/error-codes.json).
  `IsTransient` is true for `serialization_conflict` and `tenant_busy`.
- Retries: `409 serialization_conflict` is resent at most twice with jitter
  (the request rolled back), plus the client's existing pre-execution refusals
  (429 and transient 503 with Retry-After). A lost response returns
  `ErrCommitUncertain` and is never resent.
- There is no `Region` option; `Config.RegionPin` sends `X-TerraScale-Region-Pin`
  and an empty value sends no header. The client uses the one global origin.

#### Interactive transactions (WebSocket)

`db.Connect(ctx)` opens `GET /v1/projects/{project}/databases/{database}/sql/ws`
(subprotocol `terrascale.sql.v1`, `Authorization: Bearer` on the upgrade) and
returns a `*v1.SQLSession` the caller closes. `Execute`, `Query`, `Batch` and
`v1.QueryAs` take the same arguments as over REST; outside a transaction each
call is one transaction.

```go
s, err := db.Connect(ctx)
defer s.Close()
err = s.Transaction(ctx, func(s *v1.SQLSession) error {
	_, err := s.Execute(ctx, "UPDATE accounts SET balance = balance - ? WHERE id = ?", 10, from)
	if err != nil {
		return err // rolled back
	}
	_, err = s.Execute(ctx, "UPDATE accounts SET balance = balance + ? WHERE id = ?", 10, to)
	return err
})
```

- `Begin`, `Commit`, `Rollback` send the matching frames; a transaction lasts at
  most 5 seconds. `InTransaction` is the last answer's flag. After a failed
  statement calls return `ErrTransactionAborted` until `Rollback`, and `Commit`
  returns `ErrTransactionRolledBack`.
- The session is safe for concurrent use; answers are matched by frame id and
  nothing is resent. A frame holds at most 1 MiB, checked before sending.
- Errors are `*v1.SQLError` with `Status` 0. A refused upgrade (401, 403, 404,
  421, …) returns the same `*APIError` as REST.
- A lost committing reply returns `ErrCommitUncertain`; every later call returns
  the same uncertainty without reconnecting. If no committing request was sent,
  a lost open transaction returns `ErrTransactionRolledBack` until `Rollback`
  clears local state, after which a call can reconnect. After `Close` calls return `ErrSQLSessionClosed` unless a retained uncertain outcome takes precedence.
- The journey runs public SQL sessions through a network proxy to the real service,
  dropping replies and checking persisted rows, retained errors and connection counts.
- Live sample: `go run ./examples/sql` with `TERRASCALE_API_KEY`,
  `TERRASCALE_PROJECT_ID` and `TERRASCALE_DATABASE_NAME` creates a throwaway table,
  writes, reads and drops it.

`Client.ProjectUsage` and `Database.Usage` use public project/name routes and
the existing `UsageOptions`. Counts are best-effort dashboard observations,
not billing evidence.

### Database request observations

`db.RequestLogs(ctx, "2xx")` reads recent authorized request observations; pass `""`
for all status classes. The typed response preserves `DatabaseID`, `Items`, `Truncated`,
`RecordingLimited` and nullable `RequestID`. The server returns at most 100 observations
from the last 24 hours; recording is best effort. See the [pinned contract](contracts/request-logs.md).

### Legacy Management API

The hidden legacy `/v1/management` aliases still create, list, read and delete organizations and projects, and set
an organization's plan, as one ts-auth user.
`WithToken` presents the user's ts-auth access token on the client listener.
`As` names the user in `X-TerraBase-User-Id` with the management service
credential (`Config.Token`), which TerraBase accepts only on its loopback admin
listener. Take the user id from the
session, never from browser input. These existing methods retain membership
and catalog-ID semantics. New identity workflows belong in `tsauth`; public
TerraBase tooling uses the project/name methods above.

```go
m, err := tb.NewManagement(tb.Config{BaseURL: origin})
u := m.WithToken(session.AccessToken)
org, err := u.CreateOrganization(ctx, "Acme")
project, err := u.CreateProject(ctx, org.ID, "Production")
db, err := u.CreateDatabase(ctx, org.ID, project.ID, "main") // then c.Database(project.ID, "main")
usage, err := u.Usage(ctx, org.ID, project.ID, "", tb.UsageOptions{Granularity: "day", From: monthStart})
```

`Usage` reads a project (database ID empty) or one database, with UTC
`hour`/`day` buckets and optional inclusive `From`/exclusive `To`. `Period`
is nil on servers without windowed metering; lifetime counters must not be
used for a monthly bill. Organization usage is the sum of its ts-auth projects.
The pinned server accepts at most seven fractional timestamp digits; truncate
window boundaries to milliseconds before passing them to `Usage`.

It also covers `Organizations`, `Organization`, `SetPlan`,
`DeleteOrganization`, `Projects`, `Project`, `DeleteProject`, database reads
and deletes, `BranchDatabase` and `DatabaseUsage`. A 404 (`ErrNotFound`)
means an unknown id or an organization the user is not a member of.
Management errors carry structured codes, including `ErrNameTaken` for name
collisions and `ErrForbidden` for insufficient permission. `SetPlan` requires
the management service credential on the admin listener.
A rejected token or credential is `ErrInvalidKey`.

Every region that holds a database is equal. TerraBase picks which region writes.
The public region IDs are `gru`, `sjc` and `fra`. `ManagedDatabase` reports `Region` (the
answering node) and `Regions`.
`CreateDatabaseIn(ctx, org, project, name, "gru")` (or `Database.CreateIn`)
creates a database in that region; an empty region uses the called region.
Deleting a database leaves its branches independent.
Source-only, unreleased server behavior: a request reaching a region without a copy is
served through one proxy hop by a region that holds the database. A region pin selects
only the entry region and has no effect on the serving region for these calls; the proxy
removes it. An unreachable destination or stale forwarded placement returns `503 unavailable`.
See [release scope and proxy behavior](../region-proxy.md). Customer
placement/residency does not enable or disable serving regions.

### Analytics (TS-762)

`Analytics` reads TerraBase's `/v1/projects` routes using a ts-auth
**project access token**. Project and database IDs are plain ULIDs. See
[the contract](contracts/analytics.md).

```go
m, err := tb.NewManagement(tb.Config{BaseURL: origin})
u := m.WithToken(projectAccessToken)
opts := tb.MetricsOptions{Range: tb.Range24Hours, DatabaseID: databaseID, Region: "gru"}
analytics, err := u.Analytics(ctx, projectID, opts)
```

Each call returns the server's bounded window; these routes have no pagination.
TerraBase chooses time buckets for `1h`, `24h`, `7d`, or `30d`. Analytics groups
series under database/region `Items` and includes requests and operations by
type, errors, p50/p95/p99 latency, storage and client bandwidth. Nullable storage
and latency distinguish missing observations from zero. Counters are
best-effort, not billable totals. The methods share the client's context
cancellation, response bounds, API errors and safe-read retries.

### Tests

Tests use `httptest` fakes and fixtures. No test downloads or starts a
TerraBase binary, and no other repo may depend on TerraBase from this module.
Focused HTTP boundary tests check generated IDs and malformed response
envelopes without simulating persistence. Metrics fixtures reflect the pinned
producer's actual models. Fixtures verify SDK behavior, not deployed
signature/issuer enforcement, durability or retention. Existing live status/OIDC
tests require separate authorized inputs.

## TSAuth and builders

`client.TSAuth.Instance("slug").OIDC` is ts-auth's OpenID Connect provider
for applications that sign users in with the redirect flow, as the dashboard
does for single sign-on: the authorization code with PKCE, verified ID tokens,
refresh, userinfo, RFC 7009 revocation and RP-initiated logout. Customer auth
instance/issuer trust and exact registered callbacks remain explicit security
boundaries. ts-auth calls go to the default origin, `https://auth.terrascale.tech`,
and every tenant issues from `https://auth.terrascale.tech/<slug>`. There is no
regional discovery or failover. `Config.TSAuthBaseURL` names the ts-auth origin
of another deployment; an issuer must be that origin plus the slug.
The supported platform management API, API-key tokens and device flow are in
`terrascale.tech/sdk/tsauth`. Retired headless methods are described below.

SQL builders (`sdk.Select`, `sdk.ValidateSQL`) construct and validate statements
locally and do not execute them. The pgx-backed PostgreSQL client connects to
`pg.terrascale.tech:5432` using a project-scoped dashboard API key as its password.
`PostgresConfig.Database` is the database name, optionally qualified as
`<key's project ULID>/<name>`; do not URL-encode the slash. The pinned producer's
exchange requires a project claim, so use a key issued for that project.
The optional `KeyID` must match the password's key ID; otherwise it is derived.
TLS uses PostgreSQL SSLRequest negotiation, verifies the hostname and trusts
system certificate roots. Custom deployments can supply `Host`, `Port`,
`TLSServerName` and `CACertificatePEM` as explicit routing and trust inputs.
Connections have a fixed five-second deadline; a shorter caller deadline wins.
There is no plaintext fallback, reconnect or automatic mutation replay.
Ambient libpq routing, credentials and trust are ignored; a nonempty `PGSERVICE`
is rejected before pgx parses configuration or reads a service file.

The connection contract is pinned to the implemented
[credential/database authorization](https://github.com/TerraScale/terrascale/blob/a3cbf8b1c73d304189a489fa8033d41f3b508de3/src/TerraBase/Features/Postgres/PostgresConnectionHandler.cs),
[issued key syntax](https://github.com/TerraScale/terrascale/blob/a3cbf8b1c73d304189a489fa8033d41f3b508de3/src/TerraScale.Authentication/Features/Platform/ApiKeys.cs),
[public TLS edge](https://github.com/TerraScale/ts-orchestrator/blob/4c8093d0fed4b69fd31d83daa62b78301c2015e8/internal/edge/postgres.go),
and [public connection guide](https://github.com/TerraScale/terrascale/blob/894161603ff0cdeb18bb6f4a489394d197844b1f/docs/guides/postgres-clients.md).
After SSLRequest, ALPN may be absent or `postgresql`. Source and fixture checks
do not establish live authorization.

`PostgresParameter(value)` binds native pgx values or `nil` (SQL NULL); `Value()` returns native scalars, exact `pgtype.Numeric`,
`json.RawMessage` for JSON/JSONB, or `nil`; column OIDs and Int8/Text helpers remain.
The [producer scalar contract](https://github.com/TerraScale/terrascale/blob/a3cbf8b1c73d304189a489fa8033d41f3b508de3/src/TerraBase/Features/Postgres/PostgresStatements.cs)
can describe aliased CAST expressions as TEXT. `CommandTag` preserves complete tags.
Native pgx codecs use producer-supported formats. Scientific NUMERIC tokens
normalize exactly into a bounded decimal coefficient/scale without float coercion. Collected results have a fixed
16 MiB budget (wire values plus structural accounting); overflow discards results,
closes the session and returns `limit`. Decode failures abort before row cleanup
and return `decode`; partial results are discarded and mutations are never replayed. SQL/string/byte validation has a fixed
1 MiB budget less framing; the pinned producer accepts 32,767 parameters. Admission
observes cancellation. This remains one logical session: pooling is separate work.

Owned transactions hold that session exclusively: `client.Begin(ctx)` returns a
`*PostgresTx` with `Exec`, `Query` and `QueryRow` bound to the transaction, plus
`Commit` and `Rollback`. After either, every method returns `ErrTxDone`; concurrent
use from two goroutines returns `ErrTxBusy`. A failed statement leaves the
transaction usable only for `Rollback`; `Commit` then rolls back and returns the
original error.

```go
tx, err := client.Begin(ctx)
if err != nil {
	return err
}
defer tx.Rollback(ctx)
if _, err := tx.Exec(ctx, sdk.PostgresQuery{Statement: "INSERT INTO items (id) VALUES ($1)",
	Parameters: []sdk.PostgresValue{sdk.PostgresText(id)}}); err != nil {
	return err
}
return tx.Commit(ctx)
```

`client.QueryRows(ctx, query)` and `tx.QueryRows` stream instead of buffering:
iterate with `Next`/`Values`, read `Err`, and `Close` (or exhausting `Next`)
returns the session; `CommandTag` is valid once `Next` returns false.

```go
rows, err := client.QueryRows(ctx, sdk.PostgresQuery{Statement: "SELECT id FROM items"})
if err != nil {
	return err
}
defer rows.Close()
for rows.Next() {
	id, _ := rows.Values()[0].Int8()
	fmt.Println(id)
}
return rows.Err()
```

## Region latency (`client.Status`)

`RegionLatency.PingURL` is a `*string`: nil means the region has no public
browser ping target. Check it before linking; latency measurements remain
available independently of that target.

`client.Status.Latency(ctx, "")` reads all regions from the public
`https://status.terrascale.tech/api/latency` endpoint. Pass a region code such
as `"gru"` to send `?region=gru`; codes are trimmed and lowercased without
restricting the available regions. Override the origin with `Config.StatusBaseURL`
(HTTPS, or HTTP on loopback for development). Status calls
need no credentials.

```go
client, err := sdk.NewClient(sdk.Config{})
if err != nil {
    return err
}
latency, err := client.Status.Latency(ctx, "gru")
if err != nil {
    return err
}
for _, region := range latency.Regions {
    if region.Consensus != nil && region.Consensus.MedianMS != nil {
        fmt.Printf("%s: %d ms (%s)\n", region.Name, *region.Consensus.MedianMS, region.Status)
    }
}
```

`LatencyResponse.GeneratedAt` is a parsed RFC3339 timestamp; `Window` is `"1h"`.
Each region contains its code, name, ping URL, status, consensus, and individual
vantages. Median and p95 measurements are `*int` milliseconds: `nil` means no
measurement, while a pointer to zero is a measured zero. Consensus may also be
`nil`. `Samples` is an integer count. Status is `operational`, `degraded`, `down`,
or `planned`. These are probe measurements, not the caller's own browser ping.

The client shares the root SDK's fixed 30-second request deadline, 16 MiB response
cap, bounded safe-read retries and `*sdk.APIError` responses. Full `Retry-After`
is preserved; `RetryBudgetError` refuses a retry that cannot fit the deadline.
Mutations and quota failures are not replayed; redirects are refused.
The service briefly caches responses; the SDK adds no cache.

## ts-auth tokens (`terrascale.tech/sdk/tsauth`)

ts-auth's platform contract (TS-598) issues every token TerraBase accepts.
`tsauth` turns credentials into short-lived tokens:

- `NewAPIKeySource` exchanges an API key (`<ULID>_<secret>`, an OAuth client
  credential) at `{issuer}/oauth/token` for an organization or project token
  (`Project`, optional `Scopes` from the four TerraBase scopes).
- `NewExchangeSource` exchanges a signed-in user's token (RFC 8693) for an
  organization or project token, as ts-dashboard (confidential, `ClientSecret`)
  or ts-cli (public) does.

Both return a `*tsauth.TokenSource`: from 60 seconds (at most half the
lifetime) before expiry it refreshes the token in the background while callers
keep using the cached one; callers wait for a new token only at actual expiry. Concurrent callers share one request.
A refresh retries 429, 502, 503, 504 and transport failures up to three times
with full-jitter backoff or `Retry-After` inside its 30-second budget; other
failures are returned and not cached. `Invalidate(token)` drops a token a
resource server refused. It satisfies `v1.TokenSource`. Errors from the token
endpoint are `*tsauth.Error{Status, Code, Description, RetryAfter, RequestID}`. The issuer defaults to
`https://auth.terrascale.tech/terrascale`. When a `v1` client with a `BaseURL` other
than `https://api.terrascale.tech` or a `Resolver` uses the source, `APIKeyConfig.Issuer`
must name that deployment's ts-auth issuer: the client does not check where a
`TokenSource` sends the key.

```go
tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: apiKey, Project: projectID})
token, err := tokens.Token(ctx) // or pass tokens as a client's TokenSource
```

### ts-auth management

`tsauth.New(Config{Issuer})` calls the platform management API of the
tenant (`{origin}/v1/terrascale/…`) as a signed-in user, `WithToken(token)`:
organizations (`CreateOrganization`, `Organizations`, `Organization`,
`RenameOrganization`, `DeleteOrganization`), members (`Members`,
`SetMemberRole`, `RemoveMember`, `Leave`), invitations to the organization or
one project (`CreateInvitation`, `Invitations`, `RevokeInvitation`,
`LookupInvitation`, `AcceptInvitation`, `DeclineInvitation`), ownership
transfers, projects and project members, organization and project API keys
(`CreateAPIKey`, `APIKeys`, `APIKey`, `RotateAPIKey`, `RevokeAPIKey`; an empty
project means an organization key), the organization's OIDC SSO connection
(`GetSsoConnection`, `PutSsoConnection`, `VerifySsoDomain`,
`DeleteSsoConnection`; formatting never reveals the OIDC client secret) and
`AuditEvents`. Errors are
`*tsauth.APIError`, and each code is an `errors.Is` value such as
`tsauth.ErrNotFound` (also an organization the user cannot reach),
`ErrForbiddenRole` or `ErrLastOwner`.

A signed-in user also manages a project's hosted sign-in tenant through
`{origin}/v1/{project}` (viewer reads, admin changes): the end-user directory
(`ProjectUsers`, `ProjectUser`, `DisableProjectUser`, `EnableProjectUser`,
`DeleteProjectUser`; `ExportProjectUsers` reads the audited admin directory
export page by page with the producer's cursor), live end-user sessions
(`ProjectSessions` with the
producer's pagination and optional `User` filter, `RevokeProjectSession`),
OAuth clients (`ListProjectOAuthClients`, `CreateProjectOAuthClient`,
`DeleteProjectOAuthClient`) and the tenant's hosted-auth settings and social
providers. Session and user records carry canonical ULIDs; revocations are
sent once, never automatically replayed. See `contracts/project-directory.md`
and `contracts/project-oauth-clients.md` for the pinned producer contracts.

```go
c, err := tsauth.New(tsauth.Config{})
u := c.WithToken(signInToken)
org, err := u.CreateOrganization(ctx, "Acme")
project, err := u.CreateProject(ctx, org.ID, "Production")
key, err := u.CreateAPIKey(ctx, org.ID, project.ID, tsauth.APIKeyRequest{Name: "ci"})
tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: key.Key}) // key.Key is shown once
```

Management calls are not covered by local tests and do not establish deployed
platform compatibility.

### Retired headless authentication

The legacy `tsauth.NewHeadless` client and its password/email-code and JSON methods
have been removed because the current auth contract does not serve those grants
or `/headless/v1/...` routes. Use hosted sign-in or a registered OIDC client, and the
shipped `tsauth.Device` flow for the CLI, including its refresh/revoke methods.
Callbacks and grants remain client-specific; client credentials do not authorize
collecting user passwords. Shared token and error types remain available.

### ts CLI (`cmd/ts`)

`ts` signs in with `tsauth.Device` (the device grant of ts-auth's ts-cli
client) and acts as that user: projects, databases, SQL, backups, functions
and project API keys. It keeps the sign-in in
`terrascale/cli.json` under `os.UserConfigDir()` (mode 0600) and refreshes it.

```sh
go install terrascale.tech/sdk/cmd/ts@main  # @latest (v0.0.1) predates cmd/ts
ts auth login                      # approve in the browser
ts whoami && ts projects
ts db create app && ts sql app 'CREATE TABLE users (id TEXT PRIMARY KEY, email TEXT)'
ts sql app 'SELECT * FROM users LIMIT 10'
ts keys create backend app         # printed once on stdout
ts db info app
ts db create orders -regions gru,sjc
ts db placement orders -regions fra,sjc
ts db regions orders
ts backup create app && ts backup list app
ts functions deploy app hello.js
ts functions deploy app ./report -cron '0 * * * *'   # deploys report/index.js as report
ts functions invoke app hello -data '{"n":1}' && ts functions logs app hello -limit 10
ts functions list app && ts functions delete app hello
ts db branch app app-preview
ts migrate new create_orders       # writes migrations/0001_create_orders.sql
ts migrate up app-preview && ts migrate diff app-preview
ts migrate up app && ts migrate status app
ts db list
ts db delete app-preview
ts auth logout
```

Project commands act in the user's only project; `-project <name or id>`
picks one when the user reaches several. `ts help` lists every command.
Functions (`Database.DeployFunction`, `Functions`, `InvokeFunction`,
`FunctionLogs`, `DeleteFunction`) follow TerraBase's functions contract:
JavaScript `handler(event)` deployed per database, run on `invoke` or a UTC cron
schedule.
`InvokeFunction` preserves successful status, content type and response bytes;
non-2xx responses are `*v1.APIError` with `Status`, `Header` and raw `Body`.
Pass query values separately so the SDK escapes them into `event.query`:

```go
response, err := db.InvokeFunction(ctx, "hello", []byte(`{"n":1}`), url.Values{
    "label": {"a&b /+"},
})
if err != nil {
    return err
}
// Inspect response.StatusCode and response.ContentType before decoding response.Body.
```

Import `net/url` for this example. Function management needs the read and write
scopes; invocation needs the read scope.
Invocation is sent once, including handler 401/429/503 responses, and cancellation
can leave its outcome unknown. [Pinned public contract](contracts/function-invocation.md).
With `TERRASCALE_API_KEY` set (CI), database commands act as that API key in
its project instead of the stored sign-in. `ts migrate up` applies each
pending `migrations/NNNN_<name>.sql` file in one transaction together with its
row in the database's `ts_migrations` table, and refuses applied files that
changed. `ts sql` runs one SQL statement, such as reading the tables
migrations create; `ts shell` reads multiline SQL.
Invalid arguments exit with status 2, other failures with 1.

## Checks

```sh
test -z "$(gofmt -l .)"
nice -n 19 go vet ./...
nice -n 19 go build ./...
```

The module requires Go 1.25 and has no CGO dependency. CI and CLI releases use
the patched compiler pinned in `.go-version`, separately from the supported
consumer floor. See [release toolchain checks](contracts/release-toolchain.md)
for advisory scanning, artifact qualification and consumer compilation.

PostgreSQL passwords and API key token sources use
`<26-character canonical ULID>_<52 lowercase Crockford base32 characters>`
(a key may start with `sk_`): the ULID alone is the OAuth client ID.
Credential prefixes identify secret formats and are never part of an entity ID.

### Permanent API key refusals

TerraBase `401 key_revoked` and `401 key_expired` match `v1.ErrKeyRevoked` and
`v1.ErrKeyExpired` through `errors.Is`; `*v1.APIError` retains code and message.
They never retry or re-exchange the key. `tsauth.TokenSource` drops its cached
token and stays refused (including concurrent exchanges); create a new source
with a replacement key. Custom sources must implement `Reject(string, error)`
to stop future exchanges; sources with only `Invalidate(string)` have their cache dropped.
Token endpoint `invalid_client` is a terminal `*tsauth.Error`.
`503 auth_unavailable` matches `v1.ErrAuthUnavailable` and follows the existing
replay-safe request backoff and `Retry-After` policy, keeping the token cache.

Preview workflows: [CLI database branches](docs/cli-branches.md) covers create, list, guarded delete and scoped connection URLs.

### Declarative schema in the CLI

`ts schema pull app` writes a new `schema.sql` from the public SQL catalog.
Edit the CREATE statements, then run `ts schema diff app schema.sql` and
`ts schema apply app schema.sql [-dry-run] [-allow-drop]`.
Apply uses one REST transaction and refuses every DROP operation without `-allow-drop`.
`--json` returns `statements`, `destructive`, and `applied`; an unchanged schema has no statements.
Signed-in callers can pass `@` as the database to use the saved context database.

The planner validates schema-only DDL with an embedded, offline SQLite engine (no application data is stored locally).
It supports ordinary tables, indexes, views, triggers and column additions/removals. Existing column definition,
constraint, table option and column order changes require explicit `ts migrate` migrations; no inferred table rebuilds.
Engine extensions unsupported by the offline validator are refused before live writes.
See the [agent databases guide](https://docs.terrascale.tech/guides/agent-databases#manage-schema-as-code).

## Region-set placement

Create and update with one to nine distinct serving region ids in `regions`, separately from `allRegions: true`
and from a rename. Response `regions` are sorted without ordering meaning; integer `placementVersion` changes
with membership or the writer and stays unchanged for an identical set. Transitional response fields are ignored.
Region status exposes every selected region in `regions`, with nullable `role`, `revision`, `lastPulledAt`,
`lagMs` and `lastVerified` (Python uses snake_case; .NET PascalCase).
A removed regional endpoint proxies to a region that holds the database in the unreleased server contract;
see [source-only proxy behavior](../region-proxy.md). Use the global API origin.

Dropping the region ordering writes can return `503 replica_not_ready` after adding requested regions.
Additions remain selected. Repeat the same request after replication (about 30 seconds for a new region).
A repeat during that move returns the same 503; a different change returns `409 move_in_progress`.
The SDK exposes `*tb.APIError.Code` with `tb.ErrReplicaNotReady` and `tb.ErrMoveInProgress` (also `errors.Is`). Neither code is automatically retried;
general transient-503 retries do not include `replica_not_ready`, so callers can distinguish it from an outage.
The caller bounds this repeat loop to 90 seconds and propagates other errors:

`replica_not_ready` means the regions you added are stored; send the same request again. `move_unresolved` means an earlier move is unresolved; send the same request again later.

```go
ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second); defer cancel()
for { _, err := client.SetDatabasePlacement(ctx, project, database, tb.DatabasePlacement{Regions: []string{"fra"}}); if err == nil { break }; if !errors.Is(err, tb.ErrReplicaNotReady) { return err }
    select { case <-ctx.Done(): return ctx.Err(); case <-time.After(time.Second): } }
```

## Project Authentication management

Use `client.WithToken(secretKey)` for `ProjectUsers`, `DisableProjectUser` and `EnableProjectUser`. Reads require `auth:read` and changes require `auth:write`. Step-up operations require a person’s sign-in token. Keep keys in the backend.

Project MFA/session policy fields and sign-out-everywhere source APIs follow the [producer contract](../../docs/auth/mfa-sessions.md); published package qualification follows release.
Project invitation and waitlist management on this branch is described in [the auth contract](../../docs/auth/signup-restrictions.md). These additions are not yet published.
Project user profile creation, merge-patch updates, and partial password-hash imports
are available in the source SDK. See [user profiles](../../docs/auth/user-profiles.md)
for fields, scope requirements, hash encodings and the next-release API names.
Project organization management supports CRUD, memberships, emailed invitations and DNS verified domains. See [the project organizations contract](../../docs/auth/project-organizations.md).
User-owned API key creation, listing, revocation and backend verification are available in the in-repository auth client. See [the API contract](../../docs/auth/machine-tokens-and-user-api-keys.md).

### SQL session outcome rule

An SDK reports `transaction_rolled_back` only after receiving that answer from the server, or after losing an
open transaction for which no committing request was sent. If COMMIT or a request that can commit (including an
autocommit statement or batch) may have left the client and its answer is lost, the outcome is uncertain.
The SDK returns `write_outcome_unknown` (SQLSTATE `08007`) or its existing commit-uncertain error, retains that
failure for every later call on the session, and closes the connection. It never reconnects or automatically
retries an uncertain request. If the client cannot prove no request byte left, it treats the request as sent.
An execute outside a confirmed transaction, or behind unanswered transaction control, is potentially committing.
A lost read or non-committing statement inside a confirmed open transaction retains the transaction-lost behavior.
Only a confirmed refusal or a request proven not sent permits a rollback assertion; reconcile an uncertain write
through another session before deciding whether to repeat it.
