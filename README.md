# TerraScale Go SDK (`terrascale.tech/sdk`)

Go SDK with the D-13 core surface: document CRUD, typed schema builder,
typed SQL builder + raw parameterized SQL, subscribe/execute with
checkpoints and typed resets, region pinning, tenants, and management.

## Contract source

`CONTRACT_VERSION` is the committed terrabase contract pin. `make contracts`
requires `CONTRACT_URL`, an immutable versioned package URL, by default and in
CI. Before E-1 publication, development may explicitly use
`CONTRACT_SOURCE=../terrabase/contracts`; this is never an implicit fallback.
The generator verifies the producer manifest and every artifact digest, then
generates operation, route, model, and MessagePack frame metadata in
`contract.go`. The producer's HTTP, reactive, and MessagePack conformance
corpus is required as part of the same bundle; if it is absent, regeneration
fails with the E-1 publication requirement.

## Verify

```sh
make check
```

The checks exercise the pinned producer's HTTP success/error corpus and
reactive/MessagePack vectors. They also install a freshly built module ZIP
through an isolated local Go proxy and run a consumer without a checkout
replacement. These checks do not qualify a published module or a live member.

Reactive subscriptions use bounded HTTP long-polling by default. To opt into
the direct WebSocket transport, pass `SubscriptionOptions{Transport:
TransportWebSocket}` to `Subscribe`. The SDK performs the authenticated view
attach, negotiates `terrascale.sync.v1` with the view capability, and validates
each binary MessagePack frame against the pinned sync-v1 limits. A canceled or
closed connection ends the subscription; an unexpected transport or protocol
failure delivers a typed `ResetReconnect` update, after which the caller can
create a new subscription using its last checkpoint.

SQL projection numbers in `SQLRow.Value` decode as `json.Number`. Use its
`Int64` method for integer values; this preserves values above 2^53 without
rounding them through `float64`.

`Databases` exposes collection schema GET/PUT, index recommendations and deletion,
webhook endpoints and delivery replay, and resumable exports. Schema reads return
`SchemaDocument`, including migration progress; pass its generation through
`WithSchemaGeneration` and a stable `WithIdempotencyKey` when applying changes.
Index recommendations accept a limit up to 64 and retain the coordinator's
generation and truncation flag.

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

Document reads expose the exact response `ETag` and session token. Pass that
ETag to `WithIfMatch` for conditional PUT, PATCH, or DELETE; `"*"` requires
an existing document. Retain the same condition and `WithIdempotencyKey` when
retrying an uncertain write. A failed precondition returns HTTP 412 without
automatic retry. Use `Put` or `Upsert` for document replacement.
