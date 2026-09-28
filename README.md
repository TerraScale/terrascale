# TerraScale Go SDK (`terrascale.tech/sdk`)

Go SDK for the TerraScale public API. The package provides document CRUD,
typed schema and SQL builders, parameterized SQL execution, reactive
subscribe/execute with checkpoints and typed resets, discovery-driven regional
routing, tenant-scoped authorization and leases, database management, durable
imports and exports, and auth persistence.

## Atlas discovery and regional routing (ADR 0050)

Routing follows the ADR 0050 discovery-first flow:

- **Database-scoped requests** (`/api/v1/databases/{name}/…`) first resolve
  the database's eligible regional endpoints with one metadata GET,
  `https://atlas.terrascale.tech/api/v1/discovery?database=…&branch=main`,
  then connect directly to the selected `<region>.api.terrascale.tech`
  member. Documents are cached per routing scope (origin, database, branch,
  project, tenant) for the document's `refreshSeconds` (clamped to 60–3600s);
  concurrent lookups for one scope share a single request. The selected
  region is sticky across refreshes while it stays eligible; a request picks
  the first endpoint whose capabilities cover the operation (`write` for
  mutations, `read` otherwise). A transport error or a Cloudflare 52x/503
  against a regional endpoint demotes that endpoint behind the alternatives
  for safe reads and idempotency-keyed mutations. Measured latency is fed
  back into ranking: every successful request to a discovered regional
  endpoint updates a per-region EWMA (60 s halflife) that travels as the
  `X-TerraScale-Region-Rtt` header on discovery requests — never to member
  endpoints — where it outranks geography. Refreshes also send the cached
  document's revision as the `rev` query parameter for observability and
  server-side stale detection.
- **Discovery failure degrades to the region catalog**
  (`GET https://atlas.terrascale.tech/api/v1/regions`, cached five minutes):
  the request goes directly to the first available regional host. When the
  catalog cannot answer either, member routes fail with a typed
  `*sdk.RoutingError` — they never fall back to a proxy or to the bare
  product origin.
- **Product routes** (`/api/v1/organizations…`, `/api/v1/projects…`,
  `/api/v1/regions/{code}`) target the configured product origin,
  `api.terrascale.tech`, which the dashboard serves. `/api/v1/regions`
  exactly is the catalog itself and targets the atlas origin.
- **Database-less member routes** (for example database create/list and the
  reactive attach) use the catalog-selected regional endpoint; the reactive
  attach resolves through the database's discovery scope and its WebSocket
  dials the regional member origin that minted the view capability.
- **421 Misdirected Request** keeps the ADR 0043 repair-once contract (safe
  reads and keyed mutations only) and additionally adopts the member's
  authoritative placement into the routing cache, scheduling one corrective
  rediscovery (`stale=1`). `404` never triggers rediscovery.

Discovery is active when `APIBaseURL` is the public global origin (the
default) or `DiscoveryBaseURL` is configured. Custom origins, the local
emulator, and `Affinity: AffinityStrict` keep direct configured routing and
never discover. `Config.DiscoveryBaseURL` overrides the metadata origin for
tests and development (loopback dev origins may serve `http` regional
endpoints); it is independent of `APIBaseURL`.

`PinRegion`/`PreferredRegion` are the explicit region pin and
`X-TerraScale-Preferred-Region` hint; under discovery the router owns
stickiness and failover per database scope.

## Contract source

`CONTRACT_VERSION` is the committed TerraBase contract pin. Contract
verification requires `CONTRACT_URL`, an immutable versioned package URL, in
CI. Local development can explicitly use `CONTRACT_SOURCE` for a checked-out
contract bundle; there is no implicit source fallback. The generator verifies the
producer manifest and every artifact digest, then generates operation, route,
model, and MessagePack frame metadata in `contract.go`. The producer's HTTP,
reactive, and MessagePack conformance corpus is required in the bundle.

## Verify

```sh
test -z "$(gofmt -l .)"
go vet ./...
go test ./...
```

The checks exercise the pinned producer's HTTP success/error corpus and
reactive/MessagePack vectors. They also install a freshly built module ZIP
through an isolated local Go proxy and run a consumer without a checkout
replacement. These checks do not qualify a published module or a live member.

Reactive subscriptions use bounded HTTP long-polling by default. To opt into
the direct WebSocket transport, pass `SubscriptionOptions{Transport:
TransportWebSocket}` to `Subscribe`; to opt into the push stream, pass
`Transport: TransportServerSentEvents` (wire value `server-sent-events`). The
SDK performs the authenticated view
attach, negotiates `terrascale.sync.v1` with the view capability on WebSocket,
and validates each binary MessagePack frame against the pinned sync-v1 limits;
the server-sent-events transport delivers the same envelopes as base64
`data` events and tracks the event id as the resume offset. A canceled or
closed connection ends the subscription; an unexpected transport or protocol
failure delivers a typed `ResetReconnect` update, after which the caller can
create a new subscription using its last checkpoint.

## Region catalog and member health

`Regions.List` calls the atlas-owned `GET /api/v1/regions` route (the same
catalog the SDK consults internally when discovery cannot answer) and decodes
the ordered `RegionCatalogResponse` (including regional hosts). A regional
TerraBase member intentionally uses that same path shape for its
health-shaped probe; it returns `HealthResponse`, not a catalog.
`Regions.Health` and `Regions.Status` therefore call the member's
`/api/v1/health` route. Keep the two producers separate when configuring a
client: the atlas contract is pinned in `contracts/edge/v1`, while the member
contract is pinned in the TerraBase bundle. See TerraBase ADRs 0043 and 0050.

SQL projection numbers in `SQLRow.Value` decode as `json.Number`. Use its
`Int64` method for integer values; this preserves values above 2^53 without
rounding them through `float64`.

`Databases` exposes collection schema GET/PUT, schema migration rollback, index
recommendations and deletion,
webhook endpoints and delivery replay, and resumable exports. Schema reads return
`SchemaDocument`, including migration progress; pass its generation through
`WithSchemaGeneration` and a stable `WithIdempotencyKey` when applying changes.
`RollbackCollectionSchema` requires both options and accepts HTTP 202 (rollback
admitted) and HTTP 200 (idempotent replay of a completed rollback); poll
`GetCollectionSchema` until `Migration` is absent.
Index recommendations accept a limit up to 64 and retain the coordinator's
generation and truncation flag.

`GetAuthorizationLease` returns the producer lease previously authenticated for
one tenant, with its original absolute `ValidUntil` expiry. The route never
fetches identity state or renews a lease on a reader's behalf.

`Usage.GetDatabaseUsage` returns authoritative decimal-string write units and
storage byte-seconds, plus unbilled `Reads.TotalReads`. It does not estimate
current storage bytes. Export create returns the durable job identity; status
and resume reuse that job, and `DownloadExport` returns exact artifact bytes.

Database creation requires a stable `OperationID`. `CreateDatabaseResult.Database`
is populated only for HTTP 201; HTTP 202 instead populates `Operation`, which
acknowledges pending provisioning and does not imply serving readiness.
Webhook create and secret rotation return `WebhookSecretResponse`; list/delete
responses contain endpoint metadata without signing secrets. Ordinary HTTP
redirects are disabled, including for supplied HTTP clients; placement repair
uses the explicit 421 contract.

Cloud development branches expose `EnableDevelopmentFault`,
`ClearDevelopmentFault`, and `DevelopmentLogs` through the public API. Faults
require explicit branch confirmation, bounded duration and operation counts;
the server enforces branch eligibility and authorization. Schema timestamp
values use epoch-millisecond integers (`time.Time.UnixMilli()`), and byte values
use standard padded base64 JSON strings (Go `[]byte` encoding).

Use `Operations.Get` with the original operation ID to discover provisioning
progress. Reading status never resubmits database creation.

`TenantID` and `ProjectID` select the requested scope; authorization remains
server-enforced. `Documents.Create` sends `If-None-Match: *` for atomic create-only
writes. `Put` remains an upsert unless passed `WithCreateOnly`; that option is
rejected on other methods. Webhook rotation never retries automatically because
its one-time secret has no replay contract, even when shared options carry a key.

`ListExports` discovers durable jobs with bounded pagination. `CancelExport`
requests cancellation at a safe page boundary; inspect the returned
`Cancellation` state rather than assuming the job stopped immediately. Export
status retains phase, page counts and creation time alongside record progress.

`AuthPersistence` exposes the producer's strict record, unique-index, conditional
update, one-time consume, paging, and transaction operations without a framework
adapter. Supply stable operation identities. Unknown commits return the typed
error without automatic retries or callback replay; resolve using the same
transaction and idempotency identities. Auth record values remain raw JSON.

`ProjectAPIKeys` exposes the Dashboard's authenticated project API-key
create/list/rotate/revoke routes. Construct a client with the Dashboard origin
and an owner Dashboard session token, then use the one-time `Secret` returned
by `Create` or `Rotate` as `AccessToken` on a separate public API client. These
routes are not part of the producer contract and do not provide anonymous or
no-account provisioning; the Dashboard session and project membership remain
required.

`TSAuth` exposes the hosted ts-auth identity provider's management and OIDC
routes. Point a client at the deployment origin with the instance `sk_` key —
`NewClient(sdk.Config{TSAuthBaseURL: "https://auth.terrascale.tech", AccessToken:
"sk_..."})` — then call `client.TSAuth.Instance("slug")` for the scoped
families (`Users`, `Sessions`, `APIKeys`, `Organizations`, `OAuthClients`,
`Connections`, `Directories`, `JWTTemplates`, `Webhooks`, `AuditEvents`,
`Billing`, `Instance`, and `OIDC`); for example
`client.TSAuth.Instance("acme").Users.List(ctx, 50, "")`. The deployment
origin must be explicit: `TSAuthBaseURL` when set, else a non-default
`APIBaseURL`; the default product origin is rejected because it never serves
ts-auth routes. One-time secrets
(`api_key`, OAuth `client_secret`, directory `token`, webhook
`signing_secret`) are returned only by the creating or rotating call. The OIDC
family uses client credentials instead of the `sk_` key, and webhook
deliveries are verified with `VerifyTSAuthWebhook` (HMAC-SHA256 over
`timestamp + "." + rawBody`, with an optional replay tolerance). These routes
are not part of the TerraBase producer contract. The public auth origin uses
Atlas auth discovery to select a healthy regional ts-auth endpoint directly;
safe reads can fail over and mutations are sent once. Set
`TSAuthUseReverseProxy: true` to use `auth.terrascale.tech` as a reverse proxy.
Other ts-auth origins are used directly without discovery.

Document reads expose the exact response `ETag` and session token. Pass that
ETag to `WithIfMatch` for conditional PUT, PATCH, or DELETE; `"*"` requires
an existing document. Retain the same condition and `WithIdempotencyKey` when
retrying an uncertain write. A failed precondition returns HTTP 412 without
automatic retry. Use `Put` or `Upsert` for document replacement.

`Databases.GetDeletionStatus(ctx, databaseID, operationID)` reads the original
accepted deletion through its GET status route. It preserves authoritative
preparation, regional stop, and edge revocation receipts, including their exact
scope and timestamps. States are `preparing`, `stopping`, `edge_pending`, and
`cleanup_pending`; the last state does not claim physical cleanup is complete.
Canceling the status request does not cancel deletion. `DeleteDatabase` requires
HTTP 202 and `GetDeletionStatus` requires HTTP 200, with matching operation and
configured tenant/project scope in both responses.

## Supported versions

- Go floor: `go 1.24.0`, declared in `go.mod` and enforced by the toolchain at
  build time. The SDK has no CGO dependencies; runtime dependencies are
  `github.com/coder/websocket` and `github.com/zeebo/blake3` (see `go.mod`
  for the pinned versions).
- Toolchain checks: `go vet`, `gofmt`, `go test ./...` and `go test -race ./...`
  all run in CI; the dependency lane runs the pinned `govulncheck@v1.8.0`
  scanner and fails on any known vulnerability.
- Contract surface: the TerraBase API contract is pinned through
  `scripts/contracts.cs` (immutable archive with SHA-256 verification); tests
  fail closed when the archive is not resolvable. The packaged-consumer test
  exercises the module from outside this checkout.
