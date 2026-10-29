# TerraBase shape reads

`TerraBaseShapeClient` keeps a collection's local state through protocol v1
snapshot pages, sealed chunks, and live long-polls. It is additive to the
existing Documents and Auth clients and uses the same package version scheme.

```csharp
using TerraScale.DotNet.TerraBase;

using var http = new HttpClient(TerraBaseHttp.CreateHandler());
var shapes = new TerraBaseShapeClient(http, new TerraBaseShapeClientOptions
{
    BaseAddress = discoveredOrigin,
    AccessTokenProvider = GetCurrentAccessTokenAsync
});

await foreach (var state in shapes.SubscribeAsync(
    projectId, databaseName, "notes",
    where: "{\"eq\":[\"/read\",false]}",
    cancellationToken: cancellationToken))
{
    // Each state contains all applied documents and an atomic response checkpoint.
    RenderNotes(state.Documents, state.UpToDate);
}
```

The bearer credential needs `documents:read` and must authorize the project
and database in the request. Supply exactly one of `ApiKey` and
`AccessTokenProvider`, and exactly one of `BaseAddress` and `EndpointResolver`.
The credential provider and endpoint resolver run per request and must support
concurrent callers. `ITerraBaseEndpointResolver` can return a different trusted
regional origin after failover. This client does not discover regions itself.
The caller owns the `HttpClient` and must disable automatic redirects and
cookies, using `TerraBaseHttp.CreateHandler()`.

## State and recovery

Each enumeration owns its state and starts at `offset=-1`. Upsert replaces a
document and its opaque version; delete removes it. A response is validated
before any of its changes become visible. The resulting state carries the
handle, next offset, usable resume cursor, and `UpToDate`. A partial snapshot
does not expose a usable resume cursor. `UpToDate` means the server's sealed
head was reached; it does not promise read-your-writes or remote durability.

When the continuation returns `409 must_refetch` with a different handle, the
client tries one opaque resume if the snapshot is complete. A successful
resume retains local state and applies the destination's values in order.
Any failed resume or other must-refetch clears state and bootstraps again.
Resume tokens are returned byte for byte, using `TB-Shape-Resume` when the
query would exceed the protocol's request-line budget.

**Apply repeated events.** Shape values are materialized at seal time and a
repeat can contain a newer value. Deduplicating by origin/stamp/collection/id
would lose it. Shape protocol section 7.4 differs from the existing SSE
changes-feed consumer guidance.

Only documented `429 shape_busy`, `503 unavailable`, and `503 shape_too_large`
responses retry up to six consecutive times with backoff, preserving state
and offset. A `Retry-After` beyond the fixed 60-second wait limit surfaces the
error instead of retrying early. Authentication,
authorization, validation, and other errors surface to the caller. Cancellation
stops active HTTP work and retry waits. There is no background polling outside
enumeration. Request deadlines and response limits are fixed in code.

## Epoch keys and encrypted objects

`GetEpochKeyAsync` fetches the authorized handle/epoch/key-id pair from
`shape/keys` with `no-store` request semantics. It validates the returned pair
and key bytes. TBS1 decryption binds the object to the caller's authorized
handle and offset, derives the object key with HKDF-SHA256, and authenticates
every AES-256-GCM frame before accepting plaintext. Epoch and key-id mismatches,
truncation, trailing bytes, bad tags, and protocol size violations fail closed.

Object-storage transport, 302 redirects, and manifest downloads belong to
FR-22 and are excluded from this change. The origin subscription receives
plaintext JSON. TBS1 has conformance coverage in preparation for that transport.
No persistent key cache is created; callers must treat fetched keys as secrets.

## Source and qualification

The source of truth is TerraScale/terrascale commit
`993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`:

1. [shape-reads.md](https://github.com/TerraScale/terrascale/blob/993927ce7ecf8f9b889d24d4d1d21ec96eaf9416/docs/terrabase/shape-reads.md)
2. [free-reads.md](https://github.com/TerraScale/terrascale/blob/993927ce7ecf8f9b889d24d4d1d21ec96eaf9416/docs/terrabase/free-reads.md)
3. [shape-vectors.json](../contracts/terrabase/fixtures/shape-vectors.json), copied verbatim with source commit and hashes in [shape-provenance.json](../contracts/terrabase/shape-provenance.json)

Tests use only HTTP fakes and synthetic conformance vectors. No TerraBase
process or live service is run in this repository. FR-20 is wiring the owning
server routes; this SDK change does not qualify deployment or claim that
failover proofs, object mirroring, or remote durability have shipped. Shape
routes are recorded separately in [shape-routes.json](../contracts/terrabase/shape-routes.json)
from the legacy server contract pin. Wire JSON uses source generation for
trim and native-AOT compatibility.
