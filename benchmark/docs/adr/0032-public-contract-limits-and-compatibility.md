---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Public contract limits and compatibility

## Public operation coverage

`TerraScale.Database` exposes every supported customer and administrative
database operation through a named, typed REST contract. `TerraScale.DotNet`
and `TerraScale.TypeScript` expose the same operation set, authorization
requirements, errors, limits, idempotency behavior, durability choices, and
reactive behavior where it applies. An operation is not public until both SDKs,
OpenAPI, public examples, contract tests, and the service implementation agree.

The first release uses `/api/v1`. Public clients never call TBEP, FlatBuffers,
TerraBase, an OCI API, or an untyped raw HTTP escape hatch. Public reactive
traffic uses only the bounded MessagePack protocol in ADR 0025. The Database
API translates each supported public operation to a named private TBEP
operation.

## Compatibility window

TerraScale supports the current public major and the immediately preceding
public major. When a successor reaches GA, its predecessor remains supported
for twelve months. The service publishes the retirement date before successor
GA and returns standard deprecation and sunset metadata during the overlap.
Another public major cannot reach GA until the older of the two supported
majors reaches its published sunset, so the service never creates an
unmaintainable three-major overlap. Both SDKs publish supported releases and
security fixes for each admitted public major.

Additive fields and operations may ship within a public major when old clients
can ignore them without changing existing meaning. Removing a field or
operation, tightening a previously admitted value, changing canonical meaning,
or changing an error or durability outcome requires a new public major. A
deprecated contract remains fully authorized, monitored, tested, and supported
until its published retirement date. TerraScale does not promise permanent
readers, writers, or adapters for older public majors.

Private TBEP follows the `N` and `N-1` rolling-upgrade window in ADR 0031. Its
compatibility window does not extend public support and never appears in a
public negotiation.

## Stable errors

Every public failure uses one registered typed error code with a stable
meaning. Its contract states the HTTP status, retry safety, idempotency effect,
partial-outcome behavior, limit or policy involved, and safe customer message.
Reactive errors use the same catalog. SDKs expose typed errors without parsing
message text.

An existing code is never reused for another condition. Additive diagnostic
fields must not disclose internal topology, OCI identifiers, secrets, tenant
data, or private protocol details. Unknown codes remain safely representable by
old clients.

## Initial hard limits

The first public major applies these decoded, uncompressed limits before work
enters the engine:

| Resource | Hard limit |
| --- | --- |
| One materialized document | 8 MiB |
| One HTTP request or mutation batch | 16 MiB |
| Mutations in one batch | 1,000 |
| Document nesting depth | 64 |
| One public MessagePack message | 8 MiB |
| MessagePack payload chunk | 4 MiB |
| Query page size | 100 by default, 1,000 maximum |
| Query execution time | 5 seconds by default, 30 seconds maximum |
| Relationship traversal depth | 5 |
| One session token or view checkpoint | 16 KiB |
| Live reactive subscriptions per connection | 128 |
| In-flight operations per connection | 32 |

MiB and KiB mean binary units. Encoded envelopes, names, indexes, relationship
edges, attachments, and metadata count toward the applicable request and
document limit. Compression never raises a limit. Parsers reject excess depth,
length, allocation, collection count, or trailing data before materializing an
unbounded object.

Large snapshots, query results, and reset results use ordered chunks. No chunk
may exceed 4 MiB and no MessagePack envelope may exceed 8 MiB. Each stream has
a declared total bound, sequence, checksum, deadline, and cancellation path.
The receiver verifies every chunk before publication and discards incomplete
streams after the bounded retention window.

The service may impose lower tenant quotas or admission limits when the public
Production service publishes them before use. A lower quota returns a typed
limit error and does not change the protocol ceiling. SDKs apply backpressure
and bounded concurrency instead of hiding server rejection with unbounded
queues.

## Change control and evidence

TerraScale may tighten these values before the first GA release when security,
correctness, isolation, or benchmark evidence requires it. After GA, reducing
an admitted hard limit is a breaking public-contract change unless an active
security incident requires an emergency restriction. An emergency restriction
needs a recorded risk decision, public status communication, a typed failure,
and a dated recovery or major-version plan.

Qualification generates boundary corpora for every limit and exercises exact,
one-below, one-above, multiplication-overflow, truncated, duplicate-field,
deeply nested, slow-stream, cancellation, and decompression-bomb cases in the
Database API and both SDKs. Contract drift, missing SDK operations, generic raw
routes, or different error meanings block release.
