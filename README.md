# TerraScale Go SDK (`terrascale.tech/sdk`)

The SDK provides typed native document reads, queries, atomic transactions,
transaction status, and snapshot/poll subscriptions. Native requests use the
producer's UUID-scoped `/v1` protocol. Pure schema and SQL builders, product
clients, and hosted TSAuth clients are separate surfaces.

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

An explicit `DataBaseURL` disables discovery and automatic failover. It accepts
a custom HTTPS origin, a canonical `https://<region>.api.terrascale.tech` origin,
or the Atlas proxy. Credentials, paths, queries, fragments, non-ASCII hostnames
and obsolete single-level regional hosts are rejected. Explicit loopback
origins may use HTTP for development.

`DataTokenProvider` takes precedence over `DataAccessToken`. Native requests
never consult product/TSAuth `AccessToken` or `TokenProvider` credentials.
`DataDiscoveryTimeout` defaults to three seconds and accepts 1 ms–30 s within
`RequestTimeout` and the caller's deadline.

With `DataBaseURL` empty, the SDK pins the top-priority operation-eligible native8
endpoint until the earlier of `refreshSeconds` and `expires_at`. Discovery
latency consumes the lease. Scope/key fingerprints and observation floors
remain enforced across expiry and credential rotation. The bounded cache retains
up to 256 scope floors for the client lifetime.

Regional transport errors, 421 and 5xx exclude the failed region and re-resolve
Atlas. Safe operations fail over and pin the next priority; commits are never
replayed automatically. `Retry-After` and the original deadline are respected.
When discovery has a network error, 404/5xx or a non-native8 success response,
requests use `https://api.terrascale.tech`, the Atlas Worker proxy to the nearest
healthy region. Discovery retries on its normal refresh cadence (eight seconds
before the first native document), then pins as soon as native8 is available.
The hosted native producer is still pending orchestrator directory work.

Authentication/authorization denial, redirects, capability denial and regressed
observations remain `*sdk.RoutingError` failures before data dispatch. Local
failures wrap `*sdk.NativeRoutingError`; actual Atlas errors retain `*sdk.APIError`.
See [hosted routing](docs/hosted-routing.md).

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
