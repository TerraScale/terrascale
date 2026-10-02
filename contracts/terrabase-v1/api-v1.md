# Documents API v1

This is the frozen public contract for the name-based HTTPS/JSON Documents API.
It is the source of truth for tb-server, ts-go, ts-js and the Cloudflare edge.
Related contracts: [control-plane-v1.md](control-plane-v1.md),
[query-v1.md](query-v1.md), [index-format.md](index-format.md),
[auto-index.md](auto-index.md) and [shapes-v1.md](shapes-v1.md). Runtime
qualification is recorded separately in [evidence-ledger.md](evidence-ledger.md);
this document does not claim it.

## Scope

Version 1 has one writer region per database. Each database is one shard owned
by its home region, so uniqueness, `inc`, TTL reaping and index maintenance run
under that shard's writer gate. Writes reaching another region fail with
`421 home_region_required`. Multi-region online catalogs, receiver-side indexes
and global uniqueness are later gates; [document-state-v2.md](document-state-v2.md)
keeps receiver index writes disabled.

The UUID/shard routes under `/v1/tenants/{tenant}/databases/{database}/shards/…`
remain until ts-go v1 ships and are then removed without a compatibility shim.

## Transport

- HTTPS with TLS 1.3. HTTP/1.1 keep-alive and HTTP/2 (ALPN `h2`).
- Request and response JSON bodies use `Content-Type: application/json` unless a
  route names another media type. `Content-Encoding` is rejected.
- Request bodies are limited to 4 MiB. Larger bodies fail with
  `413 payload_too_large`.
- Query strings are accepted only where a route lists them. Unknown, duplicate
  or empty parameters fail with `400 invalid_request`.
- Every `u64` quantity (offsets, sequences, timestamps, byte, document and
  operation counts) travels as a canonical decimal string (no sign, no leading
  zeros). Small bounded integers (limits, rates, latencies in milliseconds,
  failure counts) and fractions are JSON numbers.
- Timestamps produced by TerraBase are canonical decimal Unix milliseconds strings.

## Authentication and tenancy

`Authorization: Bearer tbk1.<22-char base64url id>.<43-char base64url secret>`.
The key record determines the tenant, so routes never name a tenant. Key roles:

| Role | Allowed routes |
| --- | --- |
| `data` | Data plane routes in this document, within its grants. |
| `control` | `/v1/dashboard/*` only. It cannot read or write documents. |
| `edge` | `GET /v1/shapes/{shape_id}` with a valid `TB-Shape-Token`; nothing else. |

A data key has grants `{database, collections, permissions}`. `database` and
`collections` are names or `"*"`; permissions are `read`, `write` and `shape`. A
key may carry a mandatory row filter ([query-v1.md](query-v1.md) grammar) that
is ANDed into every query and shape and checked against every write it makes.
Grants are checked before the body is read and again after decoding against
every collection a request touches.

Unknown or revoked keys fail with `401 invalid_key`. A request outside the
grants fails with `403 forbidden_scope`; a key without a grant on a database
never learns whether that database exists.

## Names and identifiers

- Database and collection names match `^[a-z][a-z0-9_-]{0,62}$`.
- Document IDs are lowercase hyphenated UUIDs. `POST …/documents` generates a
  UUIDv7. Natural keys use deterministic UUIDv5 IDs computed by the client; the
  SDKs provide a helper that uses the collection name as the namespace input:
  `uuidv5(uuidv5(NAMESPACE_URL, "terrabase:collection:" + name), key)`.
- Document bodies are JSON objects. Numbers are exact decimals with at most 128
  significant digits. Depth is at most 16, nodes at most 4096 and the stored
  document at most 1 MiB. Clients must use lossless number handling.

## Versions, ETags and offsets

Every document has a `version`: `tbv1-` followed by 32 lowercase hex characters,
the first 16 bytes of SHA-256 over the canonical encoding of its current causal
context (all live and deleted siblings). Point responses send it as the strong
`ETag: "tbv1-…"`. `If-Match: "tbv1-…"` succeeds when the server's current context
has that digest; the server then uses that context as the write's observed
context. `If-None-Match: *` succeeds when the document is absent or deleted.
A failed precondition returns `412 precondition_failed`. A write without
preconditions observes the current context at the home shard and supersedes
every visible sibling.

Every write returns `offset`, the database's commit sequence for that
transaction. Offsets are totally ordered per database and are the positions used
by [shapes-v1.md](shapes-v1.md) for read-your-writes.

`durability` is `local_durable` or `object_durable`, according to the
database's acknowledgment profile.

## Idempotency

Mutating requests accept `Idempotency-Key` (1–64 characters of
`[A-Za-z0-9_.-]`). The server derives the transaction ID from the key ID, route
and idempotency key. An exact retry within 24 hours returns the original
response, including generated IDs and `offset`. The same key with a different
body fails with `409 idempotency_conflict`; after 24 hours with
`409 idempotency_expired`.

## Documents

Base path: `/v1/databases/{db}/collections/{collection}/documents`.

### `GET …/documents/{id}`

Needs `read`. Returns `200`:

```json
{"id":"0192…","version":"tbv1-…","state":"value","document":{"email":"a@b.c"}}
```

A conflicted document returns `state:"conflict"` and every sibling:

```json
{"id":"…","version":"tbv1-…","state":"conflict",
 "siblings":[{"event":{"replica":"…","incarnation":"…","sequence":"12"},"deleted":false,"document":{}},
             {"event":{"replica":"…","incarnation":"…","sequence":"14"},"deleted":true}]}
```

In a sibling `event`, `replica` is a lowercase hyphenated UUID, `incarnation`
is 32 lowercase hex characters and `sequence` is a decimal `u64` string.

Absent, deleted and TTL-expired documents return `404 not_found`. A deleted
document's 404 carries its `ETag` so a client can recreate it with `If-Match`.
`If-None-Match` with the current version returns `304`.

### `PUT …/documents/{id}`

Needs `write`. The body is the complete document object. Optional
`If-Match` or `If-None-Match: *`. Returns `201` when the document did not exist
(absent or deleted) and `200` otherwise:

```json
{"id":"…","version":"tbv1-…","offset":"42","durability":"local_durable"}
```

A PUT whose `If-Match` covers a conflicted document's whole context resolves the
conflict.

### `POST …/documents`

Needs `write`. Creates a document with a server-generated UUIDv7. Returns `201`,
the body above and `Location: /v1/databases/{db}/collections/{collection}/documents/{id}`.

### `PATCH …/documents/{id}`

Needs `write`. Optional `If-Match`. The document must exist (`404 not_found`
otherwise); a conflicted document fails with `409 conflict_unresolved`. Two
media types:

- `application/merge-patch+json`: RFC 7396 merge patch; `null` removes members.
- `application/vnd.terrabase.ops+json`:

```json
{"ops":[{"op":"set","path":"/profile/name","value":"Ana"},
        {"op":"unset","path":"/legacy"},
        {"op":"inc","path":"/db_count","by":1},
        {"op":"append","path":"/tags","value":"beta"}]}
```

Paths are RFC 6901 JSON Pointers without array indices. `set` creates missing
parent objects. `inc` treats a missing member as `0` and fails with
`409 patch_conflict` when the target is not a number; arithmetic is exact.
`append` creates a missing array and fails with `409 patch_conflict` when the
target is not an array. At most 256 ops. Patching is read-modify-write under the
home shard's writer gate. Returns `200` with the PUT response body.

### `DELETE …/documents/{id}`

Needs `write`. Optional `If-Match`. Returns `200` with
`{"id","version","offset","durability"}`; `version` is the tombstone's context.
Deleting an absent or already deleted document returns `404 not_found`.

## Transactions

`POST /v1/databases/{db}/transactions` atomically applies up to 1024 operations
across the collections of one database. Needs `write` on every written
collection and `read` on every asserted collection.

```json
{"ops":[
  {"op":"create","collection":"users","id":"…","document":{"email":"a@b.c"}},
  {"op":"put","collection":"orgs","id":"…","document":{},"if_none_match":"*"},
  {"op":"patch","collection":"projects","id":"…","ops":[{"op":"inc","path":"/db_count","by":1}],"if_match":"tbv1-…"},
  {"op":"patch","collection":"users","id":"…","merge":{"name":"Ana"}},
  {"op":"delete","collection":"sessions","id":"…","if_match":"tbv1-…"},
  {"op":"assert","collection":"plans","id":"…","filter":{"eq":["/visible",true]}},
  {"op":"assert","collection":"invites","id":"…","absent":true},
  {"op":"assert_count","collection":"access_tokens","filter":{"eq":["/user_id","…"]},"lte":24}
]}
```

- `create` is `put` with `if_none_match:"*"`; `id` is optional and generated as
  UUIDv7 when omitted.
- A `patch` op has exactly one of `merge` or `ops`.
- `assert` checks the current document without writing it: with `filter`, the
  document must exist, be unconflicted and match; with `absent:true`, it must be
  absent, deleted or expired.
- `assert_count` counts the collection's documents matching `filter` (the
  [query-v1.md](query-v1.md) grammar) and requires the count to satisfy `gte`
  and/or `lte` (at least one). It is evaluated inside the commit under the home
  shard's writer gate, before this transaction's writes, so it is serializable
  with every other write to the database. Evaluation examines at most 10000
  index entries or documents; a filter that would exceed that fails with
  `400 assert_requires_index`. At most 16 `assert_count` ops per transaction.
- Two ops may not target the same document; a duplicate target fails with
  `400 invalid_request` and `"op"` naming the later op.
- `create` takes no `if_match` or `if_none_match` (it already implies
  `if_none_match:"*"`); supplying either fails with `400 invalid_request`.
- Index entries count against an internal per-transaction write budget. A
  transaction exceeding it fails with `413 transaction_too_large`.

Response `200`:

```json
{"offset":"43","durability":"local_durable",
 "results":[{"id":"…","version":"tbv1-…"},{"id":"…","version":"tbv1-…"},
            {"id":"…","version":"tbv1-…"},{"id":"…","version":"tbv1-…"},
            {"id":"…","version":"tbv1-…"},{"asserted":true},{"asserted":true},
            {"asserted":true,"count":"3"}]}
```

Failures name the failing op by index: `412 precondition_failed` (failed
`if_match`, `if_none_match`, `assert` or `assert_count`), `409 unique_violation`,
`409 conflict_unresolved`, `409 patch_conflict` and `404 not_found` (patch of a
missing document), each with `"op":<index>` in the error object.

## Collections, uniqueness and TTL

Collections are created by the control plane. Each may declare:

- `unique`: `[{"name":"email","paths":["/email"],"transform":"lower"}]`.
  Enforced by a unique index on the home shard. Documents missing any path, or
  with `null` in any path, are not constrained. A violation returns
  `409 unique_violation` with `"constraint":"email"`.
- `ttl_path`: a JSON Pointer whose value is an RFC 3339 timestamp string or an
  integer of Unix milliseconds. A document whose TTL instant is not after the
  server clock is treated as deleted by reads, queries and transactions
  immediately. A bounded reaper tombstones expired documents (at most 256 per
  transaction); shapes observe the tombstone. A non-timestamp value at
  `ttl_path` fails with `400 ttl_invalid`. Documents without the path never
  expire.
- `schema`: `null` (schemaless, the default) or an enforced field list, see
  [control-plane-v1.md](control-plane-v1.md).

## Errors

Every error is `{"error":{"code":"…"}}`, optionally with `message`, `op`,
`constraint` or route-specific members documented with the route.

| Status | Codes |
| --- | --- |
| 304 | (no body) |
| 400 | `invalid_request`, `invalid_query`, `invalid_cursor`, `invalid_patch`, `ttl_invalid`, `sort_requires_index`, `assert_requires_index` |
| 401 | `invalid_key`, `invalid_token` (shape token rejected by the origin or edge) |
| 403 | `forbidden_scope` |
| 404 | `not_found`, `unknown_database`, `unknown_collection`, `unknown_route` |
| 405 | `method_not_allowed` |
| 409 | `conflict_unresolved`, `unique_violation`, `patch_conflict`, `index_limit_exceeded`, `idempotency_conflict`, `idempotency_expired`, `schema_changed`, `must_refetch` |
| 412 | `precondition_failed` |
| 413 | `payload_too_large`, `transaction_too_large` |
| 415 | `unsupported_media_type` |
| 421 | `home_region_required` |
| 429 | `rate_limited` (with `Retry-After`) |
| 500 | `internal_error` (an unexpected server fault; no effect is guaranteed, retry only with an `Idempotency-Key`) |
| 502 | `upstream_unavailable` (edge only: the origin failed or returned an invalid response) |
| 503 | `capacity`, `storage_unavailable`, `stored_corruption`, `commit_uncertain`, `dependencies_unavailable` |

`unknown_database` and `unknown_collection` are returned only to keys whose
grants cover the name with `"*"`; otherwise the response is `403 forbidden_scope`.
`commit_uncertain` means the client must retry with the same `Idempotency-Key`.
`capacity`, `storage_unavailable` and `dependencies_unavailable` are transient:
clients may retry reads, queries, shape reads and writes carrying an
`Idempotency-Key` with exponential backoff. `stored_corruption` is not
retryable. `429` is always retryable after `Retry-After`.

## Service endpoints

Public listener:

| Route | Purpose |
| --- | --- |
| `GET /` | `308` to `/v1/docs` |
| `GET /v1/docs`, `GET /v1/docs/protocols` | API reference |
| `GET /v1/openapi.json` | OpenAPI 3.1 for every public route in this file and its siblings |
| `GET /v1/.well-known/terrabase-edge-jwks.json` | Shape-token public keys, see [shapes-v1.md](shapes-v1.md) |

Admin listener (loopback by default):

| Route | Purpose |
| --- | --- |
| `GET /v1/healthz` | Process liveness |
| `GET /v1/readyz` | Readiness |
| `GET /v1/metrics` | Prometheus text exposition |
| `GET /v1/openapi.json` | Admin OpenAPI |

The unversioned `/healthz`, `/readyz`, `/metrics`, `/openapi.json` and `/docs`
paths are removed. ts-atlas (`src/regional-proxy.ts`, `readyPath`) and
ts-orchestrator probes must move to the versioned paths in the same rollout.

## Rate limits and connections

Each key has `rate_per_second`, `burst` and `max_in_flight`, capped by operator
configuration. Excess requests return `429 rate_limited` with `Retry-After` in
whole seconds. Long-poll and SSE requests are admitted against a separate
long-lived pool so they never starve ordinary requests.
