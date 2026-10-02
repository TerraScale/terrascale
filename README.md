# TerraScale Go SDK (`terrascale.tech/sdk`)

The SDK provides typed native document reads, queries, atomic transactions,
transaction status, and snapshot/poll subscriptions. Native requests use the
producer's UUID-scoped `/v1` protocol. Pure schema and SQL builders, product
clients, and hosted TSAuth clients are separate surfaces.

## TerraBase v1 client (`terrascale.tech/sdk/v1`)

Package `v1` is the name-based client for the frozen TerraBase v1 contracts
(`api-v1.md`, `query-v1.md`, `shapes-v1.md` and `control-plane-v1.md` in the
terrabase repository). It addresses databases and collections by name, has no
capability gate on conditional writes or multi-op transactions, and lives
beside the UUID/shard native client below, which stays until tb-server removes
the old routes.

```go
import tb "terrascale.tech/sdk/v1"

c, err := tb.New(tb.Config{BaseURL: "https://gru.terrabase.example", Token: dataKey})
users := c.Database("main").Collection("users")

res, err := users.Create(ctx, map[string]any{"email": "ana@example.com"}) // server UUIDv7
doc, err := users.Get(ctx, res.ID)
var body map[string]any
err = doc.Decode(&body) // numbers decode as json.Number, never float64

id := tb.DocumentIDFor("users", "ana@example.com") // deterministic UUIDv5 natural key
_, err = users.Put(ctx, id, body, tb.IfNoneMatchAny(), tb.IdempotencyKey("signup-ana"))
_, err = users.Patch(ctx, id, tb.Ops(tb.Inc("/logins", 1), tb.Append("/tags", "beta")), tb.IfMatch(doc.Version))
_, err = users.Patch(ctx, id, tb.MergePatch(map[string]any{"legacy": nil}))
_, err = users.Delete(ctx, id, tb.IfMatch(version))
```

Every write returns `WriteResult{ID, Version, Offset, Durability}`. Errors are
`*tb.APIError{Status, Code, Message, Op, Constraint, Handle, RetryAfter, ETag}`,
and every error code is also an error value for `errors.Is`, for example
`tb.ErrNotFound`, `tb.ErrPreconditionFailed`, `tb.ErrUniqueViolation` or
`tb.ErrMustRefetch`. A 404 for a deleted document carries the tombstone version
in `ETag`, which recreates it with `IfMatch`.

Retries: 429 is retried after `Retry-After`; `503 commit_uncertain` and
transport errors are retried only for reads, queries and mutations that carry
an `Idempotency-Key` (the retry then returns the original response). Set
`Config.MaxRetries` to change the budget.

Transactions apply up to 1024 ops atomically; failures name the op index:

```go
res, err := c.Database("main").Tx().
	Create("users", "", map[string]any{"email": "bia@example.com"}).
	Patch("projects", projectID, tb.Ops(tb.Inc("/db_count", 1)), tb.IfMatch(v)).
	Assert("plans", planID, tb.Eq("/visible", true)).
	AssertAbsent("invites", inviteID).
	Commit(ctx, tb.IdempotencyKey("tx-42"))
var apiErr *tb.APIError
if errors.As(err, &apiErr) {
	op, _ := apiErr.OpIndex() // failing op, plus apiErr.Constraint for unique_violation
}
```

Queries use filter constructors (`And`, `Or`, `Not`, `Eq`, `Ne`, `Lt`, `Lte`,
`Gt`, `Gte`, `In`, `Nin`, `Exists`, `Prefix`, `Contains`), paths as JSON
Pointers, `tb.IDPath` or `tb.Lower(path)`, and page with keyset cursors:

```go
q := tb.Query{Filter: tb.And(tb.Eq("/org_id", org), tb.Gte("/created_at", json.Number("1759363200000"))),
	Sort: []tb.SortKey{tb.Desc("/created_at")}, Project: []string{"/email"}, Limit: 50, Count: true}
for doc, err := range users.Items(ctx, q) { … }     // every match across pages
for page, err := range users.Pages(ctx, q) { … }    // page.NextCursor, page.Count
plan, err := users.Query(ctx, tb.Query{Filter: f, Explain: tb.ExplainAnalyze}) // plan.Explain is raw JSON
```

Shapes are reactive queries. `Subscribe` calls the gatekeeper and follows the
log: `offset=-1` → 307 → snapshot pages → log chunks → live long-polls echoing
`TB-Shape-Cursor`. A `409 must_refetch` yields an `EventReset` (discard local
state) and restarts from a snapshot. `WithSSE()` reads the origin's
server-sent events instead (resuming with `Last-Event-ID`); `ViaEdge()` reads
through the edge URL with the shape token and refreshes it through the
gatekeeper before `expires_at`. `WaitFor` gives read-your-writes:

```go
sub, err := db.Subscribe(ctx, tb.ShapeRequest{Collection: "notifications", Filter: tb.Eq("/user_id", uid)})
defer sub.Close()
go func() {
	for ev, err := range sub.Events(ctx) { apply(ev) } // EventChanges, EventUpToDate, EventReset
}()
res, _ := notifications.Put(ctx, id, doc)
err = sub.WaitFor(ctx, res.Offset) // the consumer has handled this write
```

The control plane uses a control key. Every mutation takes an explicit
idempotency key, which the server requires; keep it to retry the same
mutation, or generate one with `tb.NewIdempotencyKey()`:

```go
d, err := tb.NewDashboard(tb.Config{BaseURL: origin, Token: controlKey})
t, err := d.CreateTenant(ctx, tb.NewIdempotencyKey(), tb.CreateTenantRequest{Name: "Acme", ExternalRef: "proj_123"})
db, err := d.CreateDatabase(ctx, tb.NewIdempotencyKey(), t.ID, tb.CreateDatabaseRequest{Name: "main"})
key, err := d.CreateKey(ctx, tb.NewIdempotencyKey(), t.ID, tb.CreateKeyRequest{Name: "server",
	Grants: []tb.Grant{{Database: "main", Collections: []string{"*"}, Permissions: []string{"read", "write", "shape"}}}})
op, err := d.DeleteDatabase(ctx, tb.NewIdempotencyKey(), t.ID, "old")
op, err = d.WaitOperation(ctx, op.ID, time.Second)
```

It also covers usage, collections and stats, indexes and index decisions, key
rotation and revocation, edge keys, advisors, regions and audit.

The v1 tests run in the normal `go test -race ./...` suite against an in-test
HTTPS contract fixture (`v1/fixture_*_test.go`) that implements the contract's
wire format; it will be replaced by the packaged tb-server once the v1 routes
ship.

## Native data client

Configure the provisioned native `tbk1` credential. The SDK asks Atlas for an
authorized regional endpoint for each exact UUID/shard scope:

```go
client, err := sdk.NewClient(sdk.Config{
    DataAccessToken: os.Getenv("TERRASCALE_DATA_KEY"),
})
if err != nil {
    return err
}
scope := sdk.DocumentScope{
    Tenant:   "11111111-1111-1111-1111-111111111111",
    Database: "22222222-2222-2222-2222-222222222222",
    Shard:    sdk.DecimalU64(1),
}
point, err := client.Documents.Get(ctx, scope,
    "33333333-3333-3333-3333-333333333333",
    "44444444-4444-4444-4444-444444444444")
if err != nil {
    return err
}
// Inspect point.State, point.StateVersion, point.Context, and every sibling.
```

Supply the actual provisioned scope and collection/document UUIDs. UUIDs use
canonical lowercase text; shard IDs and other u64 fields retain the full unsigned
range and serialize as decimal JSON strings.

Atlas discovery always uses `https://atlas.terrascale.tech/api/v1/discovery`,
with the exact tuple and the same captured native key used for the data request.
`DiscoveryBaseURL`, region preferences, and product selectors do not alter native
admission. Native discovery and data never send cookies or follow redirects.

An explicit `DataBaseURL` bypasses discovery for source preparation or a separately
authorized deployment. It requires HTTPS and an ASCII hostname; credentials,
paths, queries, fragments, trailing-dot aliases, and reserved TerraScale service
hosts are rejected. TerraScale endpoints require a lowercase direct regional
hostname; independent HTTPS origins remain available for source preparation.
Explicit loopback development origins may use HTTP. Direct-origin fixtures do
not qualify public runtime routing.

`DataTokenProvider` takes precedence over `DataAccessToken`. An empty provider
result fails discovery locally and sends no credential to an explicit origin.
Native requests never consult `AccessToken` or
`TokenProvider`, which belong to the product and TSAuth management surfaces.

With `DataBaseURL` empty, discovery requires a canonical bare `tbk1` key and a
strict current Atlas response. The SDK selects the first source-ranked endpoint
that supports the requested operation. Metadata leases are bound to the tuple
and private key fingerprint, last at most ten seconds, and subtract discovery
latency. Future publications and publication intervals longer than ten seconds
are rejected. Observed deployment, cell, publisher generation, and revision floors
survive expiry and key rotation. Up to 256 tuple floors are retained for the
client lifetime; capacity fails closed rather than forgetting a floor.

Discovery failure returns `*sdk.RoutingError` before native dispatch. Local
failures wrap `*sdk.NativeRoutingError`, whose `Code` includes
`capability_unavailable`; actual Atlas errors retain `*sdk.APIError` and the
server status/code. There is no global API, static catalog, or stale lease
fallback and no automatic native operation replay.
See [hosted routing](docs/hosted-routing.md) for endpoint selection and error handling.

Point reads require READ; queries also require QUERY; snapshot/poll also require
SUBSCRIPTIONS. Leaderless Transaction Status requires WRITE, including reconciliation of a
retained conditional or multiwrite operation after a capability downshift. It
does not additionally require the original advanced mutation capabilities.
SingleWriter Status requires admitted security/writer fencing and remains
unavailable through discovery in this packet. The
current receiver advertises no conditional, atomic-write-group, or security
capability and no security writer. Conditional/multiwrite commits remain locally
unavailable through discovery; projected subscription groups remain read delivery.

## Values and point states

`Documents.Get` preserves `absent`, `deleted`, `value`, and `conflict` states,
all siblings, and observed context. A single live or deleted sibling has an
exact `StateVersion`; absent and conflict states have no version token. A
conflict remains a conflict until the application deliberately resolves it.

Use `NullValue`, `BoolValue`, `I64Value`, `UTF8Value`, `BytesValue`,
`MapValue`, and `ArrayValue` to construct tagged values. Signed integers
serialize as decimal strings, bytes as unpadded base64url, and map fields use
numeric IDs. Bytes and child slices are owned by the value. Constructors and
wire decoding enforce the producer's depth, child, node, and encoded document
limits; an array cannot contain another array through any descendant.

`Documents.Query` uses a typed scalar or presence predicate with numeric field
paths. Query rows retain full points and arrive in increasing document UUID
order. Pagination cursors are opaque and must be reused with the same scope and
predicate.

## Atomic transactions and recovery

Build a transaction with `NewTransactionID` and `NewCommitRequest`. Each write
carries the provisioned schema generation/digest, a condition, observed context,
and live fields or a tombstone. Conditions are `none`, `absent`, or
`version`; a retained tombstone requires its exact version. The SDK computes
the producer's canonical effect digest and freezes the request before dispatch.

`Documents.PrepareTransaction` returns an owned `PreparedTransaction`.
Its `Request` and `StatusRequest` accessors return copies. `Commit`,
`Status`, and explicit `Retry` retain the original scope, identity, effects,
and originating client even if the caller later edits its input.

Inspect `*sdk.CommitError` when a commit fails. If `Uncertain` is true, retain
`Operation` and query its status. A transport failure, unexpected success
status, redirect, or malformed receipt can leave a dispatched mutation
uncertain. The SDK never automatically replays a commit.

Only committed or rejected status is definitive. `unknown`, `pending`, and
`expired` do not prove abort and never authorize a fresh transaction identity.
A successful receipt preserves the producer's `local_durable` or
`object_durable` claim without upgrading it. All native successes require
HTTP 200 and a valid JSON response.

Single-writer requests fail locally until authenticated writer admission and a
real security fence are integrated. Membership routing does not establish that
fence.

## Snapshot and poll subscriptions

`Documents.Subscribe(scope, collection, predicate, limit)` creates a
subscription with an owned predicate. Call `Next(ctx)` to fetch snapshot pages
until the response provides a poll cursor, then to fetch atomic poll groups.
The SDK preserves each group and validates ordering, bounds, and cursor
progress. Empty-effect groups still advance the retained sequence and cursor.

A null effect point means the document left the selected view; it does not
assert deletion of the underlying document. Apply a group's effects together.
Keep the final poll cursor to use with `ResumeSubscription` on the same scope
and predicate; the producer verifies the cursor's binding and retention.

Cancellation stops the current request. There are no background subscription
workers. A producer `resync_required` response is an error; obtain a new
snapshot instead of treating it as an empty result.

## Product, TSAuth, and builders

Product calls require an explicit `APIBaseURL`. `AccessToken` and
`TokenProvider` configure their management credential. `ProjectAPIKeys`
uses the authenticated Dashboard project API-key routes; a one-time returned
secret can be explicitly supplied as a native client's `DataAccessToken`.
The routes require the owning session and project membership.

TSAuth calls require an explicit `TSAuthBaseURL`, or a suitable explicit
`APIBaseURL`. Use `client.TSAuth.Instance("slug")` for the scoped families.
Management methods use the instance credential. The OIDC application API uses
explicit issuer and OAuth client authentication, verifies authorization-code
responses and ID tokens, and exposes introspection, revocation, and logout
flows. It does not inherit a native data credential. Accepted OIDC source
checks do not qualify an actual hosted-provider journey.

Schema and SQL builders construct and validate statements locally and do not
execute SQL over HTTP. The explicit pgx-backed PostgreSQL client uses separate
listener configuration, credentials, and CA trust. Actual PostgreSQL execution
requires its producer protocol and an admitted signed listener; source and
package checks do not qualify an actual admitted PostgreSQL or native runtime.
Retained product methods do not establish management, provisioning, or restore
runtime qualification.

## Producer artifacts and checks

`contracts/native/client-openapi.json` and `atomic_vectors.json` are exact
copies of the actual TerraBase source artifacts. Their SHA-256 digests and source
paths are recorded in `contracts/native/manifest.json`. `contract.go` contains
native operation, route, and model metadata derived from that OpenAPI.
The separate edge artifacts and pins remain in `contracts/edge/v1`.

Verify committed artifacts and generated metadata:

```sh
python3 scripts/native_contracts.py --check
test -z "$(gofmt -l .)"
go vet ./...
go build ./...
go test -race ./...
```

To reproduce the pinned native copies from the explicitly selected producer
checkout, run `python3 scripts/native_contracts.py --source ../terrabase`.
The generator verifies the admitted exact source hashes before writing any
artifact. Updating those pins requires the actual successor producer bytes.

The Go tests include malformed wire/scope/value/status cases, the actual
producer effect vectors, frozen uncertain recovery, and a consumer installed
through an isolated local module proxy. These are source and package checks.
No native contract archive or published-module qualification is claimed.
The pinned Atlas Worker/SQLite success and forbidden artifacts exercise the
source consumer with synthetic publication sequences and a fixed test clock.
Actual Atlas runtime admission, the actual provider/customer journey,
PostgreSQL execution, and the C02/C06/C08 runtime gates remain open.

The module requires Go 1.25 and has no CGO dependency. CI runs the pinned
vulnerability scanner alongside formatting, vet, build, artifact verification,
and race tests. BLAKE3 remains a dependency of the retained product artifact
verification.

## Resource OAuth

A registered resource uses its own introspection credentials. Originating OAuth
clients obtain opaque access tokens for one exact HTTPS resource URI. The URI's
raw path and query remain part of its identity. Management keys register policy;
they do not substitute for either OAuth credential role.

```go
origin, err := instance.NewResourceClient(sdk.TSAuthResourceClientConfig{
    Issuer: "https://auth.terrascale.tech/example",
    ResourceAudience: "https://api.example.test/management",
    Authentication: sdk.TSAuthOAuthClientAuthentication{
        ClientID: originClientID, ClientSecret: originSecret,
        Method: sdk.TSAuthClientSecretBasic,
    },
})
if err != nil { return err }
grant, err := origin.RequestClientCredentials(ctx, []string{"management:read"})
if err != nil { return err }
resource, err := instance.NewResourceIntrospector(sdk.TSAuthResourceIntrospectorConfig{
    Issuer: "https://auth.terrascale.tech/example",
    ResourceAudience: "https://api.example.test/management",
    Authentication: sdk.TSAuthResourceAuthentication{
        ClientID: introspectionClientID, Secret: introspectionSecret,
    },
})
if err != nil { return err }
principal, err := resource.Introspect(ctx, grant.Tokens.AccessToken)
if err != nil { return err }
// Authorize principal against current durable grants in the application.
```

`OAuthClientPolicies()` and `OAuthResourcePolicies()` expose explicit allowlists,
checked numeric `uint64` epochs, secret rotation and disable operations. Secrets
are returned once. Nonrotating client PATCH returns a client record; a rotating
PATCH returns the client plus its new secret. Resource PATCH always returns a
resource record and a nullable secret.

`NewResourceApplication` adds resource selection to verified browser OIDC while
retaining server-held state, nonce and PKCE. The application atomically consumes
an attempt before exchanging its callback. ID-token audience remains the
originating client. Device authorization exposes one explicit poll at a time and
honors the provider interval. Code, device and refresh redemption can inherit
or repeat their bound URI; they cannot select another resource. Refresh scope
`nil` inherits the current grant; a nonnil empty slice narrows it to no scopes.

Serving-resource introspection requires Basic authentication and validates the
configured issuer, exact audience, Bearer type, integer times and principal shape.
Users require a nonempty session ID. Services require null session ID and subject
matching originating client ID. Inactive results grant no access. Subject,
scopes and activity do not establish project or provisioner ownership; current
durable grants remain application authorization. Introspection uses a fresh
request with no browser cookie jar or management/native token provider.

Unknown exchanges and refresh rotations return `TSAuthOIDCError.Uncertain` and
are never automatically replayed. Resource contracts pin the independently
reviewed protocol source and actual loopback HTTP response captures. Those
fixtures verify source interoperability; native persistence, two-process
revocation and hosted caller qualification have separate evidence.
