# Shapes v1: reactive queries and edge reads

A shape is a filtered, projected view of one collection, served as an
append-only log over HTTP. Its snapshot pages and closed log chunks are
immutable, so a CDN can cache them for a year and serve them without contacting
the origin. Live updates use long-polls whose URLs change with a time bucket so
the CDN collapses concurrent clients into one origin request. This follows the
ElectricSQL HTTP shape model. Nothing is purged on commit: new data appears only
at new offsets.

## Gatekeeper

`POST /v1/databases/{db}/shapes` with a data key holding `shape` on the
collection:

```json
{"collection":"notifications","filter":{"eq":["/user_id","…"]},"project":["/title","/read"]}
```

The filter uses [query-v1.md](query-v1.md) without sort, limit or cursor. The
gatekeeper stores nothing, so clients may retry it like a read. The
key's row filter, if any, is ANDed in. Response `201`:

```json
{"shape_id":"9f2c…","handle":"h1-…","offset":"-1",
 "url":"https://edge.terrascale.tech/v1/shapes/9f2c…?offset=-1",
 "token":"eyJ…","expires_at":"1759364100000"}
```

`shape_id` is 32 lowercase hex characters: the first 16 bytes of SHA-256 over
`"TB-SHAPE-V1\0"`, the tenant, database and collection IDs, the canonical filter
and projection, and the collection generation. Clients with the same effective
shape share its cache entries.

`handle` identifies one log lineage for the shape: it changes when the
collection generation changes or the retention floor passes a client's
position.

## Shape tokens

`token` is a compact JWS (EdDSA over Ed25519):

```json
{"alg":"EdDSA","typ":"JWT","kid":"…"}
{"iss":"terrabase","aud":"terrabase-edge","tid":"<tenant>","db":"<database>","shp":"<shape_id>","iat":1759363200,"exp":1759364100}
```

Tokens live at most 15 minutes; clients call the gatekeeper again before
`expires_at`. Public keys are published as a JWKS (`kty:"OKP"`, `crv:"Ed25519"`)
at `GET /v1/.well-known/terrabase-edge-jwks.json`. Two keys are published during
rotation. A revoked data key's tokens remain valid until they expire.

## Reading the log

`GET /v1/shapes/{shape_id}?handle=…&offset=…[&live=true&cursor=…]`

Parameters, which must appear at most once each:

| Name | Value |
| --- | --- |
| `offset` | `-1`, a snapshot position `snap.<S>.<P>`, or a log position (decimal `u64`) |
| `handle` | required unless `offset=-1` |
| `live` | `true`, only with a log position |
| `cursor` | decimal live bucket, only with `live=true` |

Authorization is either a data key with `shape` on the collection (direct
origin access) or, through the edge, the shape token (see "Edge").

### Snapshot

`offset=-1` returns `307` to `?handle=<handle>&offset=snap.<S>.0`, where `S` is
the current head, with `Cache-Control: public, max-age=0, s-maxage=5`.
Snapshot pages list the shape's documents at snapshot `S` in document-ID order,
up to 1000 documents or 1 MiB per page (a single document larger than that is
sent as a page of its own). Each page is immutable.

### Log chunks

A log position `O` returns the shape's changes in commits after `O`. A chunk
from `O` covers commits up to `O + 4096` and ends early, before the commit that
would make its body exceed 1 MiB. A commit whose changes alone exceed 1 MiB is
sent as a chunk of its own; no response body ever exceeds 8 MiB. It is **closed** when it ends at the byte limit or when the head has
reached `O + 4096`; otherwise it is the **open tail** and ends at the head.

### Body

```json
{"shape_id":"9f2c…","handle":"h1-…","offset":"120","next_offset":"133","up_to_date":false,
 "changes":[{"offset":"125","ops":[
   {"op":"upsert","id":"…","version":"tbv1-…","document":{"title":"Hi","read":false}},
   {"op":"upsert","id":"…","version":"tbv1-…","state":"conflict","siblings":[…]},
   {"op":"delete","id":"…"}]}]}
```

A document leaving the shape (deleted, expired by the reaper or no longer
matching) produces `delete`. Commits with no matching change are omitted, but
the offsets still advance. A snapshot page uses the same body with
`offset:"snap.S.P"`, a single change at offset `S` containing only `upsert` ops,
and `next_offset` set to `snap.S.(P+1)` or, after the last page, `S`.
An empty snapshot is a single page whose change has `ops:[]`.
`up_to_date` is `true` when `next_offset` equals the head.

### Caching headers

| Response | `Cache-Control` |
| --- | --- |
| Snapshot page, closed chunk | `public, max-age=60, s-maxage=31536000, immutable` |
| Open tail chunk | `public, max-age=0, s-maxage=1` |
| Live response | `public, max-age=0, s-maxage=<seconds left in the bucket>` |
| `307`, errors | as above for `307`; `no-store` for errors |

Headers on every `200`: `TB-Shape-Handle`, `TB-Shape-Next-Offset`,
`TB-Shape-Up-To-Date` (`true`/`false`), `ETag: "<shape_id>:<handle>:<offset>:<next_offset>"`;
live responses add `TB-Shape-Cursor`.

### Live mode

With `live=true`, if the head is past `O` the response is the open tail
immediately. Otherwise the origin waits up to 20 seconds for a commit; when none
arrives it returns `200` with no changes, `next_offset:O` and `up_to_date:true`.
The live bucket is `floor(unix_seconds / 20)`. Clients send the
`TB-Shape-Cursor` value from their previous response, and the server returns the
current bucket, so concurrent clients at the same offset request the same URL
within a bucket. `cursor` is optional on a client's first live request.

### Must-refetch and retention

When `O` is below the retention floor, the handle is stale or the collection
generation changed, the origin returns `409` with
`{"error":{"code":"must_refetch","handle":"<new handle>"}}` and `no-store`. The
client discards its state and restarts from `offset=-1` without a handle. Retention is
configured per database by bytes and age (defaults 1 GiB and 7 days).

### Read-your-writes

Writes return `offset` ([api-v1.md](api-v1.md)). A client that needs its own
write waits until a received `next_offset` is at least that offset.

## Server-sent events

`GET /v1/shapes/{shape_id}/sse?handle=…&offset=…` (origin only, data key with
`shape`) streams the same bodies as events: `event: changes` (data: a chunk or
snapshot page body) with `id: <next_offset>`, `event: up-to-date` (data:
`{"next_offset":"…"}`) and `event: must-refetch` (data: `{"handle":"…"}`,
then the stream ends). `offset=-1` streams the snapshot pages directly, without
a redirect; `handle` is required otherwise. `Last-Event-ID`, when present,
overrides `offset`. A comment line is sent every 15 seconds. SSE responses are
`no-store`.

## Edge

The Worker in `cloudflare/shapes-edge`, deployed with the `cf` CLI on
`edge.terrascale.tech`, serves `GET /v1/shapes/{shape_id}` and CORS preflight.
Every other path returns `404`.

1. It requires `Authorization: Bearer <shape token>`, verifies the EdDSA
   signature with WebCrypto against the cached JWKS, and checks `aud`, `exp` and
   that `shp` equals the path's shape ID. An invalid or expired token returns
   `401 invalid_token`; a valid token for another shape returns
   `403 forbidden_scope`. Neither contacts the origin.
2. It canonicalizes the query (parameter order `handle`, `offset`, `live`,
   `cursor`) and uses the URL without credentials as the cache key, shared by
   every authorized client.
3. A cached response is returned directly. On a miss, the request is routed to
   the `ShapeHub` Durable Object named by the shape ID, which makes at most one
   origin request per URL at a time and fans the response out to every waiting
   client before writing it to the cache.
4. Origin requests use an `edge`-role key and forward the shape token in
   `TB-Shape-Token`; the origin verifies it again.

Reads served from the cache never reach TerraBase. Cloudflare still bills Worker
and Durable Object requests.
