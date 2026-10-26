# TerraScale Go SDK (`terrascale.tech/sdk`)

Package `v1` is the TerraBase client for project JWTs: databases, collections,
documents, queries, transactions, relational SQL and database change streams.
It also implements the proposed shape-reads v1 client, with local conformance
coverage; deployed shape endpoints are not qualified. See the
[shapes contract](contracts/shapes.md) for protocol provenance and limits.
`tsauth` exchanges customer API keys and user tokens for project access tokens.
The root package holds hosted TSAuth clients and pure schema and SQL builders.
The [compatibility matrix](contracts/terrabase-compatibility.md) records the
exact server revision, local test evidence and remaining qualification gaps.

Document and platform entity IDs are canonical uppercase, 26-character Crockford ULIDs. Use
`sdk.NewID()` (or `v1.NewID()`) to generate one and `ValidateID` to validate it.
Generation is concurrency-safe and monotonic within a millisecond. Entity
IDs reject UUIDs, prefixes and lowercase forms. Document keys use the same
contract: every document ID is a canonical ULID, sent once as the `id` query
parameter and rejected otherwise. An application payload may contain its own `id`.
OAuth client names, database and
collection names, region/service catalog names, versions and credentials
remain protocol names or opaque tokens, not entity IDs.

SQL and PostgreSQL primary keys are user-chosen values, including integer, text
and composite keys; they do not need to be ULIDs. Relational `QuerySQL` and
`BatchSQL` preserve these values. Document point operations still require ULIDs.
Shared list/query/change/shape/export results legitimately carry either document
ULIDs or SQL primary keys. Stored keys follow the producer's 1–256 UTF-16 code
unit, non-control-character contract; export resume keys use that same contract.
Document point inputs and API entity references continue to require ULIDs.

The SDK owns no persistent collections, databases or KV records. This contract
alignment requires no stored-data deletion or migration.

## Source and module availability

The published `v0.0.5` module ([source `b2b7a55a`](https://github.com/TerraScale/ts-go/tree/b2b7a55a66da4dd0db6588d27d571efe2177f31c))
includes full-text/vector queries but predates `Database.Export`. Current source includes
streaming document export and newer fixes; install an immutable commit with
`go get terrascale.tech/sdk@<full-commit>` and retain the generated pseudo-version.
Use that revision's API and contract files. Source, fixture and package build results do
not establish production qualification for every method. Releases remain `0.0.X`.

## Zero-configuration operating contract

The SDK has no regional discovery or failover. Auth calls go to the fixed
global origin `https://auth.terrascale.tech`, the default API origin is
`https://api.terrascale.tech`, and ts-orch on OCI handles routing. No region
activation, endpoint pinning or manual secret copying belongs in setup.
Customer tenant/project scope and authorized access credentials remain required.
The examples describe SDK boundaries; deployed issuer trust still requires
qualification. See [AGENTS.md](AGENTS.md#zero-configuration-user-decision-2026-10-09-do-not-revert).

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
err = db.Delete(ctx)                        // DELETE; needs databases:write
_, err = db.CreateCollection(ctx, "users") // 409 name_taken when it exists
cols, err := db.Collections(ctx, tb.ListOptions{Limit: 50})
users := db.Collection("users")             // also Info and Drop

res, err := users.Create(ctx, map[string]any{"email": "ana@example.com"}) // server ULID
doc, err := users.Get(ctx, res.ID)
var body map[string]any
err = doc.Decode(&body) // numbers decode as json.Number, never float64

id := tb.NewID() // retain this key when resolving an uncertain outcome
_, err = users.Put(ctx, id, body, tb.IfNoneMatchAny())
_, err = users.Patch(ctx, id, tb.Ops(tb.Inc("/logins", 1), tb.Append("/tags", "beta")), tb.IfMatch(doc.Version))
_, err = users.Patch(ctx, id, tb.MergePatch(map[string]any{"legacy": nil}))
_, err = users.Delete(ctx, id, tb.IfMatch(version))
page, err := users.List(ctx, tb.ListOptions{Cursor: next}) // id order
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
Legacy `tb_` keys remain usable only where the server still accepts them.

### Unique keys

Declare the key on an empty collection, then let TerraBase reject duplicates
instead of checking first. A duplicate write, or any transaction containing
one, fails as a whole:

```go
db := c.Database(projectID, "main")
_, err = db.DeclareUniqueKeys(ctx, "users",
    []tb.UniqueKey{{Paths: []string{"email"}, Normalization: tb.NormalizationTrimLowercase}})
_, err = db.Collection("users").Create(ctx, map[string]any{"email": "ana@example.com"})
if errors.Is(err, tb.ErrUniqueViolation) { /* taken; nothing was written */ }
```

Declaring fails with `tb.ErrSchemaRequiresEmptyCollection` once the collection
holds documents. `db.UniqueKeys` reads the declared keys back.

The schema routes take a ts-auth project access token, not a `tb_` API key:
use it as `Config.Token` with `databases:read` (reads) or `databases:write`
(declares) plus `documents:write` for the writes.

Select an explicit acknowledgement guarantee with `col.Put(ctx, id, doc, tb.RemoteDurable)`
or `db.Tx().Put("users", id, doc).WithDurability(tb.RemoteDurable).Commit(ctx)`.
The default is local durability; inspect the returned receipt rather than assuming a
remote request proves replication. See [write durability](contracts/write-durability.md)
for preexecution refusal and uncertain-outcome semantics; uncertain writes never replay.

Every write returns `WriteResult{ID, Version, Offset, Durability}` and the
version is the `ETag`. Versions are opaque tokens (`tbv1-…`, `tbv2-…`):
compare and pass them back, never parse them. Unknown `state` and
`durability` values and new response members decode without error. Errors are `*tb.APIError{Status, Code, Message, Op,
RetryAfter, ETag, Body, RequestID}`, and every error code is also an error value for
`errors.Is`, for example `tb.ErrNotFound`, `tb.ErrPreconditionFailed`,
`tb.ErrUnknownDatabase` or `tb.ErrNameTaken`. A 404 for a deleted document
carries the tombstone version in `ETag`, which recreates it with `IfMatch`.
`Body` preserves the complete error response, including malformed JSON;
`RequestID` preserves an `X-Request-ID` response header when supplied. Truncated
or oversized error responses retain bounded bytes and an unwrap-able read
cause. Successful typed responses reject empty, null and malformed envelopes.

Retries: TerraBase does not deduplicate retries and rejects `Idempotency-Key`,
so the client never sends one. Only requests a replay cannot apply twice are
retried: reads, read-only SQL, queries and writes with an `IfMatch`
version (a transaction when every write op has one). `IfNoneMatchAny` is not
enough, since a tombstone left by another writer satisfies it again. For
those, 429 is retried after `Retry-After`, and the transient rejections
(`capacity`, `storage_unavailable`, `dependencies_unavailable`) with backoff.
Transport errors and `commit_uncertain` are retried only for reads and
queries. Mutations return uncertain outcomes without a replay: use
`errors.Is(err, tb.ErrCommitUncertain)` and re-read the state before deciding
what to do. A transport failure matches that code and retains its original
cause; a server response retains its `*tb.APIError`. Other writes and
`500 internal_error` are never retried. Ordinary requests have a fixed 30-second total
budget and 64 MiB response cap; shape exchanges have a 45-second budget. Attempts are finite.
A server cooldown is never shortened: `RetryBudgetError` retains the typed API error and
full cooldown when it cannot fit the remaining deadline. Earlier caller cancellation wins. Public
database creation is POST and is never replayed after a transient rejection.
Point document operations use `/documents/by-key?id=…`, with the document's
ULID as the query value. Keys never enter path segments.
Listing and generated creation use GET and POST `/documents`, respectively.
The old `/documents/{id}` point routes are removed.
The server implements If-Match/ETags and relational SQL, including writes and DDL through
`ExecuteSQL`, `QuerySQL` and `BatchSQL`. It does not provide mutation deduplication or a
universal RFC 9457 response; preserve the documented typed errors and uncertain outcomes.

### Collection shapes

Subscribe to one collection, optionally using the existing query filter helpers.
Each update is the current local state after a whole response and includes its
checkpoint. Breaking the loop stops requests; cancelling `ctx` stops a long-poll
or backoff wait. A refused resume emits an empty reset before bootstrapping again.

```go
for state, err := range users.Subscribe(ctx, tb.ShapeOptions{Where: tb.Eq("/active", true)}) {
    if err != nil { return err }
    // Persist documents and checkpoint together if the application needs storage.
    render(state.Documents, state.UpToDate)
}
```

The client pages a snapshot, reads sealed chunks, then long-polls with live
cursor buckets. OCI may change the serving machine under the same global API
origin: one proven cursor resume retains state; refusal drops it and restarts.
Repeated events are applied because their materialized value can be newer.
Snapshot page cursors become usable only after the entire snapshot.

`users.ShapeKey(ctx, handle, epoch, keyID)` fetches an authenticated no-store
epoch key, reused in process memory by `(handle, epoch, keyID)` within the client's
collection scope. Past epochs and shape/key 409 responses evict cached keys;
keys are never persisted. The returned key's `Decrypt(offset, ciphertext)`
verifies and opens TBS1 AES-256-GCM bytes for that authorized binding.
The origin subscription consumes plaintext JSON; manifests,
302 and automatic object-storage fetching are deferred until FR-22.

### Transactions

Transactions apply up to 1024 ops atomically; failures name the op index:

```go
res, err := db.Tx().
	Create("users", "", map[string]any{"email": "bia@example.com"}).
	Patch("projects", projectID, tb.Ops(tb.Inc("/db_count", 1)), tb.IfMatch(v)).
	Assert("plans", planID, tb.Eq("/visible", true)).
	AssertAbsent("invites", inviteID).
	Commit(ctx)
var apiErr *tb.APIError
if errors.As(err, &apiErr) {
	op, _ := apiErr.OpIndex() // failing op
}
```

Queries use filter constructors (`And`, `Or`, `Not`, `Eq`, `Ne`, `Lt`, `Lte`,
`Gt`, `Gte`, `In`, `Nin`, `Exists`, `Prefix`, `Contains`, `WithinRadius`,
`WithinBox`), paths as JSON
Pointers or `tb.IDPath`, and page with keyset cursors:

```go
q := tb.Query{Filter: tb.And(tb.Eq("/org_id", org), tb.Gte("/created_at", json.Number("1759363200000"))),
	Sort: []tb.SortKey{tb.Desc("/created_at")}, Project: []string{"/email"}, Limit: 50, Count: true}
for doc, err := range users.Items(ctx, q) { … }     // every match across pages
for page, err := range users.Pages(ctx, q) { … }    // page.NextCursor, page.Count
```

`tb.Near(path, vector, tb.Cosine|tb.L2)` sorts by vector distance, nearest
first. It must be the only sort key, with `Limit` at most 1000 and no cursor;
each returned `Document.Distance` holds the distance:

```go
res, err := docs.Query(ctx, tb.Query{Sort: []tb.SortKey{tb.Near("/embedding", vec, tb.Cosine)}, Limit: 10})
```

Vector indexes take one dotted field path and dimensions from 1 to 4096:

```go
dimensions := 768
index, err := db.CreateIndex(ctx, tb.IndexRequest{
	Collection: "items", Fields: []string{"embedding"},
	Kind: tb.IndexVector, Dimensions: &dimensions,
})
```

Creation may return `index.State == "building"` while existing documents are indexed.
`db.Indexes(ctx)` reports when it becomes `active`; near queries scan until then.
A matching active vector index makes near results approximate.

Geo points are `{"lat": …, "lon": …}` objects (WGS 84 degrees).
`tb.WithinRadius(path, lat, lon, meters)` and
`tb.WithinBox(path, south, west, north, east)` are ordinary filters (a box with
`west > east` crosses the antimeridian). `tb.NearPoint(path, lat, lon)` sorts
by meters, nearest first, under the same rules as `Near`; `Distance` holds the
meters:

```go
res, err := shops.Query(ctx, tb.Query{Filter: tb.WithinRadius("/loc", -23.55, -46.63, 1000),
	Sort: []tb.SortKey{tb.NearPoint("/loc", -23.55, -46.63)}, Limit: 20})
```

`Query.Aggregate` groups matches by 0-4 paths and returns `QueryResult.Groups`
(empty `Items`, no cursor). It takes no `Sort`, `Project`, `Cursor` or `Count`;
`Limit` bounds the groups and more groups fail the query. Each `Group` has a
`Key` (one JSON value per path), a `Count`, and `Sum`/`Avg`/`Min`/`Max` maps
from pointer to `*float64` (nil when the group has no numbers):

```go
res, err := orders.Query(ctx, tb.Query{Filter: tb.Gte("/created", 1761264000000),
	Aggregate: &tb.Aggregate{GroupBy: []string{"/status"}, Sum: []string{"/amount"}, Avg: []string{"/amount"}}})
for _, g := range res.Groups { fmt.Println(string(g.Key[0]), g.Count, *g.Sum["/amount"]) }
```

`Collection.SetTTL(ctx, "/expiresAt")`, `TTL` and `DeleteTTL` manage document
expiry. Documents expire when that member holds a past RFC 3339 UTC time; an
hourly sweep deletes them, so removal can lag by up to an hour.

### Public database tooling

`Client.Databases` lists project databases; `Database.Info` reads metadata by
name. `Database.SQL`, `CheckSQL` and `ExplainSQL` use `/v1/projects/{project}/databases/{name}/sql`
and its `/check` and `/explain` routes with `databases:read`. SQL is the server's
read-only SELECT subset, with a default LIMIT of 100, a maximum of 1000 and an
8 MiB result bound. `CheckSQL` can return `OK: false` for an unsupported
statement; invalid explain requests return API errors. Root SQL builders do
not execute this HTTP API. New code should use relational SQL below; the
read-only `Database.SQL` route refuses dashboard API keys.

### Relational SQL

`ExecuteSQL`, `QuerySQL` and `BatchSQL` call
`POST /v1/projects/{project}/databases/{name}/sql/execute`
([guide](https://docs.terrascale.tech/guides/sql-http)). Each call is one
transaction: a batch of 1 to 256 statements applies completely or not at all,
and `BEGIN`/`COMMIT`/`ROLLBACK` are refused. Arguments are `nil`, booleans
(bound as 1/0), numbers and strings; `v1.Named` binds `:name`, `@name` or
`$name` parameters.

```go
db := c.Database(projectID, "main")
_, err := db.ExecuteSQL(ctx, "INSERT INTO users (id, name) VALUES (?, :name)", 1, v1.Named("name", "Ada"))
res, err := db.ExecuteSQL(ctx, "SELECT id, name FROM users WHERE id = ?", 1)
// res.Columns[0].Name == "id"; res.Rows[0][0] == int64(1); res.LastInsertRowID is *int64
rows, err := db.QuerySQL(ctx, "SELECT id, name FROM users") // []map[string]any
out, err := db.BatchSQL(ctx, []v1.SQLStatement{
	{SQL: "UPDATE users SET name = ? WHERE id = ?", Args: []any{"Grace", 1}},
	{SQL: "DELETE FROM users WHERE id = :id", Named: map[string]any{"id": 2}},
})
var sqlErr *v1.SQLError // Code, Message, Statement (0-based), SQLiteCode
if errors.As(err, &sqlErr) { /* sql_error, sql_conflict, sql_too_large, forbidden_scope */ }
```

Row values are `nil`, `int64`, `float64` or `string`; integers decode exactly
(the SDK never routes them through float64). `SQLError` wraps the `*APIError`,
so `errors.Is(err, v1.ErrSQLConflict)` also matches. Other failures stay
`*APIError`. Requests are never retried: a lost response returns
`ErrCommitUncertain`. `TestSQLLive` runs against production when
`TERRASCALE_SQL_LIVE_TOKEN`, `TERRASCALE_SQL_LIVE_PROJECT` and
`TERRASCALE_SQL_LIVE_DATABASE` are set.

`Client.ProjectUsage` and `Database.Usage` use public project/name routes and
the existing `UsageOptions`. Counts are best-effort dashboard observations,
not billing evidence.

### Database changes

`Database.Changes(ctx, ChangesOptions{Cursor: "now", Collections: []string{"users"}})`
returns an iterator of `change` and `cursor` events from `GET /v1/projects/{project}/databases/{name}/changes`.
Consume every cursor checkpoint; retain `ChangeEvent.Cursor` for a later
subscription. Clean EOF reconnects with `Last-Event-ID`, the original endpoint
and collection filter. The server gives that header precedence over the
initial query cursor. Documents are the serving machine's current value,
possibly null; deduplicate by origin, version, collection and key.

Cancellation, malformed/truncated events and terminal SSE `error` events stop
iteration. `ErrCursorExpired` is returned without resetting to `now` or
re-reading customer data. Streams fail visibly if they end before providing a
resume cursor. Collection/index metadata and dropped collections' former
documents are absent from the feed.

### Legacy Management API

The hidden legacy `/v1/management` aliases still manage organizations, projects, databases, members,
invitations, ownership transfers and the audit log as one ts-auth user.
`WithToken` presents the user's ts-auth access token on the client listener.
`As` names the user in `X-TerraBase-User-Id` with the management service
credential (`Config.Token`), which TerraBase accepts only on its loopback admin
listener, for bootstrap work such as `AddMember`. Take the user id from the
session, never from browser input. These existing methods retain membership
and catalog-ID semantics. New identity workflows belong in `tsauth`; public
TerraBase tooling uses the project/name methods above.

```go
m, err := tb.NewManagement(tb.Config{BaseURL: origin})
u := m.WithToken(session.AccessToken)
org, err := u.CreateOrganization(ctx, "Acme")
project, err := u.CreateProject(ctx, org.ID, "Production")
db, err := u.CreateDatabase(ctx, org.ID, project.ID, "main") // then c.Database(project.ID, "main")
inv, err := u.CreateInvitation(ctx, org.ID, "bia@example.com", tb.RoleAdmin)
page, err := u.Members(ctx, org.ID, tb.PageOptions{After: next})
usage, err := u.Usage(ctx, org.ID, project.ID, "", tb.UsageOptions{Granularity: "day", From: monthStart})
```

`Usage` reads a project (database ID empty) or one database, with UTC
`hour`/`day` buckets and optional inclusive `From`/exclusive `To`. `Period`
is nil on servers without windowed metering; lifetime counters must not be
used for a monthly bill. Organization usage is the sum of its ts-auth projects.
The pinned server accepts at most seven fractional timestamp digits; truncate
window boundaries to milliseconds before passing them to `Usage`.

It also covers `Organizations`, `RenameOrganization`, `SetPlan`,
`DeleteOrganization`, `CurrentOrganization`/`SetCurrentOrganization`, project
and database reads, renames and deletes, `BranchDatabase`, `SetMemberRole`,
`RemoveMember`, `Leave`, `Invitations`, `RevokeInvitation`,
`AcceptInvitation` (the access token's verified email must match),
`OwnershipTransfer` and its start, cancel and confirm,
`AuditRetention`/`SetAuditRetention`, `AuditEvents`, and the read-only
database tooling: `Collections` (document counts and bytes), `DatabaseUsage`
and `SQL` (rows of the dashboard SQL subset), secondary indexes
(`CreateIndex`, `Indexes`, `DropIndex`; a duplicate is `ErrIndexExists`), and
project data API keys:
`CreateProjectAPIKey` (the `tb_…` secret is returned only once),
`ProjectAPIKeys` and `RevokeProjectAPIKey`. A 404 (`ErrNotFound`)
means an unknown id or an organization the user is not a member of.
Management errors carry structured codes, including `ErrNameTaken` for name
collisions and `ErrForbidden` for insufficient permission. `SetPlan` requires
the management service credential on the admin listener.
A rejected token or credential is `ErrInvalidKey`.

Writable databases require at least two regions, identified by region ids
such as `gru-1` (not `RegionCode`). `ManagedDatabase` reports `Region` (the
answering node), `HomeRegion` and `ReplicaRegions`.
`CreateDatabaseIn(ctx, org, project, name, tb.Placement{HomeRegion: "gru-1",
ReplicaRegions: []string{"sjc-1"}})` places a new database. An empty home uses
the called region for management; it does not request single-region placement.
Omitting `replicaRegions` uses every other current serving region. An explicit
replica list must include at least one region other than the home; an empty
list on create returns 400 `placement_needs_two_regions`. `SetDatabaseRegions`
replaces the replica list (the home never changes).
`tb.Placement{AllRegions: true}` and `SetDatabaseAllRegions` place a full copy
in every serving region, including regions that join later
(`ManagedDatabase.AllRegions`; `ReplicaRegions` is then empty). It cannot be
combined with `ReplicaRegions`; `SetDatabaseRegions` turns it off.
Delete directly in the database's home region without a preceding placement
update. Permanent purge authority removes every replica, including all-regions
placement; branches remain independent. A 204 response records logical deletion
and local cleanup. Remote erasure is retried durably, so 204 does not guarantee
synchronous remote erasure. An empty replica-list update can temporarily make
document writes return 409 `placement_needs_two_regions`; it is not required
for deletion and does not provide supported single-region placement.
A region outside the placement answers 421
`ErrWrongRegion`, on Management and Documents routes alike, and the
`*tb.APIError` carries `HomeRegion` and `Regions`. The client never retries
it elsewhere: send the request to a region in `Regions`. Customer
placement/residency does not enable or disable serving regions.
The current legacy placement path still requires per-replica project
registration: call `PrepareProjectRegistration(ctx, org, project)` using that
region's admin listener, management service credential and `As(userID)`, then
`registration.Commit(ctx)`. Supplied IDs require the service credential.
That manual path is an inventoried implementation gap, not first-party startup
guidance or proof of automatic node admission. Its handle captures one ULID and
the missing organization/project operations under their home-region IDs.
If Commit returns `ErrCommitUncertain`, retry the same handle with a fresh
context; it resends the exact body without repeating existence checks or
creating a new commit ID. Retain the handle until the outcome is resolved.

Public `v1` methods cover backups, branches/placement, indexes, JSON schema, saved
queries and query shapes. `Query.Explain` returns the public document-query plan;
SQL Explain is a separate operation. Method presence does not establish deployed
JWT compatibility or complete live coverage. Collection subscriptions follow the
[shape protocol](contracts/shapes.md), independently qualified from the .NET shape
client. `Idempotency-Key` remains unsupported.

### Replication and analytics (TS-762)

`Analytics` and `Replication` read TerraBase's `/v1/projects` routes using
a ts-auth **project access token**. Project and database IDs are plain ULIDs.
The wire shape is reconciled with the pinned server implementation; see
[the contract](contracts/replication-analytics.md).

```go
m, err := tb.NewManagement(tb.Config{BaseURL: origin})
u := m.WithToken(projectAccessToken)
opts := tb.MetricsOptions{Range: tb.Range24Hours, DatabaseID: databaseID, Region: "gru"}
analytics, err := u.Analytics(ctx, projectID, opts)
replication, err := u.Replication(ctx, projectID, opts)
```

Each call returns the server's bounded window; these routes have no pagination.
TerraBase chooses time buckets for `1h`, `24h`, `7d`, or `30d`. Analytics groups
series under database/region `Items` and includes
requests and operations by type, errors, p50/p95/p99 latency, storage, client
bandwidth and directed replication bandwidth. Replication reports period ops
and bytes by region pair, lag, divergence, convergence time, last applied and
status per database and region. Replication bandwidth carries its traffic
`Kind`; nullable storage, latency and boolean divergence distinguish missing
observations from zero or false. Counters are best-effort, not billable totals.
The methods share the client's context cancellation, response bounds, API
errors and safe-read retries.

### Tests

Tests use `httptest` fakes and fixtures. No test downloads or starts a
TerraBase binary, and no other repo may depend on TerraBase from this module.
Focused HTTP boundary tests check generated IDs and malformed response
envelopes without simulating persistence. Metrics and SSE fixtures reflect the
pinned producer's actual models. Fixtures verify SDK behavior, not deployed
signature/issuer enforcement, durability, replication or retention. Existing
live status/OIDC tests require separate authorized inputs; no new skips were
added. The matrix records checks and unverified producer dependencies.

## TSAuth and builders

`client.TSAuth.Instance("slug").OIDC` is ts-auth's OpenID Connect provider
for applications that sign users in with the redirect flow, as the dashboard
does for single sign-on: the authorization code with PKCE, verified ID tokens,
refresh, userinfo, RFC 7009 revocation and RP-initiated logout. Customer auth
instance/issuer trust and exact registered callbacks remain explicit security
boundaries. ts-auth calls go to one fixed global origin, `https://auth.terrascale.tech`,
and every tenant issues from `https://auth.terrascale.tech/<slug>`. There is no
regional discovery or failover. `TSAuthBaseURL` overrides the origin only for
dev and test deployments; an issuer must be that origin plus the slug.
The supported platform management API, API-key tokens and device flow are in
`terrascale.tech/sdk/tsauth`. Retired headless methods are described below.

Schema and SQL builders construct and validate statements locally and do not
execute SQL over HTTP. The pgx-backed PostgreSQL client connects to
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
and [public connection guide](https://github.com/TerraScale/ts-docs/blob/894161603ff0cdeb18bb6f4a489394d197844b1f/docs/guides/postgres-clients.md).
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
observes cancellation. This remains one logical session: pooling and owned
transactions are separate work. Never share manual BEGIN/COMMIT groups between callers or replay uncertain mutations.

## Region latency (`client.Status`)

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
[`status_example_test.go`](status_example_test.go) contains a compilable example.

Unit tests use `httptest`. The live contract test requires authorized test inputs and skips HTTP 404
until the endpoint is deployed:

```sh
TERRASCALE_STATUS_LIVE=1 nice -n 19 go test . -run '^TestStatusLatencyLive$' -v
```

Before merging this client, require the deployed endpoint to match the response,
region filter, public CORS, and cache contract (404 now fails):

```sh
TERRASCALE_STATUS_REQUIRE_LATENCY=1 nice -n 19 go test . -run '^TestStatusLatencyLive$' -v
```

## ts-auth tokens (`terrascale.tech/sdk/tsauth`)

ts-auth's platform contract (TS-598) issues every token TerraBase accepts.
`tsauth` turns credentials into short-lived tokens:

- `NewAPIKeySource` exchanges an API key (`<ULID>_<secret>`, an OAuth client
  credential) at `{issuer}/oauth/token` for an organization or project token
  (`Project`, optional `Scopes` from the four TerraBase scopes).
- `NewExchangeSource` exchanges a signed-in user's token (RFC 8693) for an
  organization or project token, as ts-dashboard (confidential, `ClientSecret`)
  or ts-cli (public) does.

Both return a `*tsauth.TokenSource`: it caches the token until 60 seconds
before it expires, concurrent callers share one request, failures are not
cached, and `Invalidate(token)` drops a token a resource server refused. It
satisfies `v1.TokenSource`. Errors from the token endpoint are
`*tsauth.Error{Status, Code, Description, RetryAfter}`. The issuer defaults to
`https://auth.terrascale.tech/terrascale`.

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
project means an organization key) and `AuditEvents`. Errors are
`*tsauth.APIError`, and each code is an `errors.Is` value such as
`tsauth.ErrNotFound` (also an organization the user cannot reach),
`ErrForbiddenRole` or `ErrLastOwner`.

```go
c, err := tsauth.New(tsauth.Config{})
u := c.WithToken(signInToken)
org, err := u.CreateOrganization(ctx, "Acme")
project, err := u.CreateProject(ctx, org.ID, "Production")
key, err := u.CreateAPIKey(ctx, org.ID, project.ID, tsauth.APIKeyRequest{Name: "ci", Scopes: []string{tsauth.ScopeDocumentsWrite}})
tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: key.Key}) // key.Key is shown once
```

Management tests use the pinned local contract,
`tsauth/testdata/platform-api.openapi.json`. They do not establish deployed
platform compatibility; see the qualification limits in the matrix.

### Retired headless authentication

The legacy `tsauth.NewHeadless` client and its password/email-code and JSON methods
have been removed because the current auth contract does not serve those grants
or `/headless/v1/...` routes. Use hosted sign-in or a registered OIDC client, and the
shipped `tsauth.Device` flow for the CLI, including its refresh/revoke methods.
Callbacks and grants remain client-specific; client credentials do not authorize
collecting user passwords. Shared token and error types remain available.

### ts CLI (`cmd/ts`)

`ts` signs in with `tsauth.Device` (the device grant of ts-auth's ts-cli
client) and acts as that user: projects, databases, collections, documents,
read-only SQL, replica regions, backups, functions and project API keys. It keeps the sign-in in
`terrascale/cli.json` under `os.UserConfigDir()` (mode 0600) and refreshes it.

```sh
go install terrascale.tech/sdk/cmd/ts@main  # @latest (v0.0.1) predates cmd/ts
ts auth login                      # approve in the browser
ts whoami && ts projects
ts db create app && ts sql app 'SELECT * FROM users LIMIT 10'
ts doc put app users '{"email":"ana@example.com"}'
ts doc list app users > users.jsonl && ts doc import app-copy users < users.jsonl
ts keys create backend app         # printed once on stdout
ts db regions app sjc fra && ts db info app
ts backup create app && ts backup list app
ts functions deploy app hello.js -trigger-collection items -events upsert,delete
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
`FunctionLogs`, `FunctionDeliveries`, `DeleteFunction`) follow TerraBase's
functions contract: JavaScript `handler(event)` deployed per database, run on
`invoke`, collection changes or a UTC cron schedule.
With `TERRASCALE_API_KEY` set (CI), database commands act as that API key in
its project instead of the stored sign-in. `ts migrate up` applies each
pending `migrations/NNNN_<name>.sql` file in one transaction together with its
row in the database's `ts_migrations` table, and refuses applied files that
changed. `ts exec` runs one relational SQL statement, such as reading the
tables migrations create.
Invalid arguments exit with status 2, other failures with 1.

## Checks

```sh
test -z "$(gofmt -l .)"
nice -n 19 go vet ./...
nice -n 19 go build ./...
nice -n 19 go test -race ./...
```

The module requires Go 1.25 and has no CGO dependency. CI and CLI releases use
the patched compiler pinned in `.go-version`, separately from the supported
consumer floor. See [release toolchain checks](contracts/release-toolchain.md)
for advisory scanning, artifact qualification and consumer compilation.

PostgreSQL passwords and API key token sources use
`<26-character canonical ULID>_<52 lowercase Crockford base32 characters>`:
the ULID alone is the OAuth client ID. Credential prefixes identify secret
formats and are never part of an entity ID.
