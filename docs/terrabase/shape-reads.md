# Shape reads (TS-474)

- Status: implemented for TS-474; divergences requested by the ticket are recorded below
- Scope: `src/TerraBase/Features/Documents` (`GetShapeEndpoint`, `IShapeService`, `ShapeService`),
  `src/TerraBase/Engine` (`IStorageTransaction.SnapshotAsync`, `IStorageTransaction.ChangesAsync`, `IDatabaseStore.WaitForCommitAsync`, schema v8)
- Contracts: `terrabase-rust/docs/shapes-v1.md`, `query-v1.md`

## Summary

A shape is one collection and an optional filter, synced as a snapshot followed by a change log.
Every response is addressed by `(handle, offset)`. This follows the Electric HTTP shape model
(<https://electric.ax/docs/api/http>, <https://electric.ax/docs/guides/auth>):

- Snapshot pages and closed log chunks never change meaning and carry stable `ETag`s.
  Nothing is purged on commit; new data appears only at new offsets. Origin responses require
  private cache revalidation so every reuse checks current authorization.
- Live long-polls carry a time-bucket `cursor`, so clients at the same offset request the same
  URL within a bucket and the origin collapses concurrent reads. Shared CDN caching requires
  an edge authorization check before every cache lookup; that component is not implemented.
- A stale handle answers `409 must_refetch`; the client discards its state and starts again.

## Route

`GET /v1/projects/{project}/databases/{database}/collections/{collection}/shape?where=&handle=&offset=&live=&cursor=`

Served on the client listener (TLS) with `Authorization: Bearer <ts-auth project access JWT>`.
The token must grant `shapes:read`, its `project_id` must match `{project}`, and any `database`
claim must match `{database}`. TerraBase validates it against the configured platform issuer's
JWKS. Legacy `tb_` keys with `shapes:read` remain a hidden compatibility path until COS confirms
migration. OpenAPI tag `Live`.

| Parameter | Value |
| --- | --- |
| `where` | optional query v1 filter as JSON (`query-v1.md`); no sort, limit, cursor or project. Absent = every document |
| `offset` | required: `-1`, `snap.<S>.<P>` or a decimal log position `O` |
| `handle` | required unless `offset=-1` |
| `live` | `true`, only with a log position |
| `cursor` | decimal live bucket, only with `live=true` |

Validation, all `400 invalid_request` unless noted:

- Only these parameters, each at most once and non-empty.
- Offsets have one spelling: canonical decimals (no sign, no leading zeros) and the canonical
  unpadded base64url of the id's UTF-8 for `P`. Anything else is not an offset.
- `where` that is not JSON or not a valid filter is `400 invalid_query`. A shape read whose filter
  exceeds the engine's query budget (5 million SQLite VM instructions or 5 seconds, the same budget
  as document queries) is `400 invalid_query` too, for snapshot pages and log chunks alike.
- An unknown database is `404 unknown_database`; an unknown collection is `404 unknown_collection`.

## Offsets on the change log

The shape history is an append-only `change_log`, separate from the current `documents` table.
Shape offsets use the per-database SQLite commit clock, independently of peer journal positions.
Replication applies winning document changes and advances shape history in the same transaction
as its applied frontier. Imported changes retain their document merge versions; public ETags also
identify the raw body and deletion state, so equal-version winners invalidate stale validators. Each
receiving transaction gets one local history offset above its previous head and at least as high
as the versions it observes. Equal-version winning changes therefore appear after the old shape
head. Imports are not captured again into the sending journal. Commits that advance the clock
also wake live shape readers.

The current mapping is:

- Every put, tombstone and delete of a client database appends its document or tombstone to
  `change_log` at the transaction's per-database commit offset, in the same storage transaction.
  Multiple writes of the same document in one transaction remain separate operations, in their
  original order (`version`, then append sequence). Current-generation history is never compacted
  or reaped. A write whose collection has no metadata is refused, and its transaction cannot commit.
- The replication journal admits at most 1024 final document states and 2 MiB of content in a
  local commit (`503 capacity`, with the operation index). This can reject distinct writes before
  the shape framing limit; repeated mutations of one key still count toward shape admission,
  while the journal retains only that key's final state. Both bounds fail atomically.
- `_system` (API keys, Management) keeps no shape history and has no per-commit shape limits:
  clients cannot name it, and Management cleanup may delete more than 1024 entries at once.
- Dropping a collection removes every body of its generation from `change_log` in the same
  transaction and keeps the entries as tombstones; the generation's handles already answer `409`.
- Schema v5 (on TS-459's v4) adds `change_log`, its indexes `change_log_by_offset (collection,
  generation, version, sequence)` and `change_log_by_document (collection, generation, id, version,
  sequence)`, the `shape_history` floor and a random `collections.generation`. Existing client
  databases seed their current documents and tombstones as a baseline (`_system` seeds none), repair missing collection metadata, and set
  the floor to the head at migration: earlier states cannot be reconstructed and answer `409`. A new
  database has floor `0`. `IStorageTransaction.GetHistoryFloorAsync` reports the oldest valid offset.
- Schema v6 adds `change_log_births (collection, generation, id, version)`, a `WITHOUT ROWID` key
  holding each document's first write per generation, and `change_log_high (collection, generation,
  version, id)`, a row each time a new id sorts above every earlier id of the generation. The trigger
  `change_log_birth` adds both on the document's first `change_log` insert. Upgrading from v5
  backfills them from each id's earliest retained version. Offsets grow in commit order, so the
  generation's highest id at `S` is that of its latest `change_log_high` row at or before `S`.
- Schema v7 preserves v5 history and adds `change_log.document_version`: the document version
  used by ETags and shape upserts. `change_log.version` remains the local history offset used
  for snapshots, log windows and commit grouping. Existing entries seed `document_version`
  from their original offset; imports can carry an older merge version at a newer history offset.
- Handles bind TS-459's persisted database incarnation (`IDatabaseStore.Incarnation`, drawn
  again on every create and copy) and the collection generation, so a recreated collection or
  database under the same name starts a new lineage.
- Creating or dropping a collection is a commit of its own offset, like a document write. If the
  offset cannot be allocated, the transaction cannot commit: the change and its offset are one unit.
  Read transactions refuse mutations, and a refused or failed write refuses the commit.
- `IStorageTransaction.SnapshotAsync` reads only the append-only change log, never live tables, in
  one streamed statement per page: it walks `change_log_births` in id order after `P`. Each id first
  written at or before `S` is a step: one seek for its last write at or before `S`, skipped when that
  is a tombstone or the filter rejects it, so a step costs two index seeks however long the document's
  history is. An id first written after `S` costs its births entry only (its last-write lookup has a
  false `version <= S` term): its revisions are never read, the filter never runs, and it is not a
  step, so later writes cannot change a page, its boundaries or its ETag. The walk stops at the
  highest id at `S` (one `change_log_high` seek per page), so ids appended after `S` in id order,
  such as server-generated ULIDs, are never visited. Later ids that sort between `S`'s ids
  (client-chosen ones) are passed over: a page passes over at most 50000 of them; beyond, it
  answers `409 must_refetch`: the snapshot is too old to read within bounded work, and a restart at
  `-1` snapshots the head. `S` is checked
  against the head in the same read transaction before the history query: a future `S` also answers
  `409 must_refetch`. `ChangesAsync` replays
  operations after `O`; a written document the filter rejects comes back as a delete.
  Its active enumerator exclusively owns the transaction: other operations, another stream, and
  transaction disposal fail promptly with `InvalidOperationException`. Complete or dispose the
  enumerator before using the transaction again; a refused write still prohibits commit.

| Offset | Meaning |
| --- | --- |
| `-1` | `307` to `snap.<S>.0`, where `S` is the head when the redirect is made |
| `snap.<S>.<P>` | snapshot page of head `S` after document `P` (`0` for the first page) |
| `O` | log position: every change through commit offset `O` was delivered |

`P` is a keyset (the last document id of the previous page, base64url-encoded so legacy ids with
dots or URL delimiters survive the URL), not the contract's page index.
Historical reads keep that order stable after rewrites and deletes.

**Snapshot pages** hold up to 1000 documents or 1 MiB of serialized body in id order, from the live
matching documents **as of `S`**. `next_offset` is `snap.<S>.<base64url(last id)>`, or `S` after the last page. A page examines at
most 10000 ids, and its 5 s deadline is checked between steps as well as inside SQLite: a filter
that matches few documents can return a page with no ops whose `next_offset` continues after the
last id it examined.
The page's single change has offset `S` and only `upsert` ops; an empty snapshot is one page with
`ops:[]`. One maximum-size document can exceed 1 MiB once framing is added: it is an allowed
singleton page, bounded by 8 MiB. Page admission measures complete serialized bodies, including
actual offsets, versions, separators and framing.

**Log chunks** cover the commits in `(O, O + 4096]`, grouped per commit offset, with each document's
last write of a commit only: every write of a commit shares its version, so only the final state may
carry it. Each commit is
serialized first. The empty serialized `ShapeBody` at the fixed window end supplies its actual
framing bytes; serialized commits and their separators are added to that count. Using the fixed
window's offset length makes byte boundaries independent of the current head. A chunk stops before
the commit that would take that count past 1 MiB; a single larger commit travels alone, bounded by
8 MiB. Documents retain their stored bytes, including literal characters such as `<`.
A chunk is **closed** when cut by bytes or when the head is at least `O + 4096`; otherwise it is the
**open tail** and ends at the head. Commits without document operations in the collection are omitted,
but `next_offset` still advances past them. Offsets below the history floor or above the head answer
`409 must_refetch`.

## Why immutable cached copies remain identical

Snapshot URLs name a fixed historical state at `S`. Closed log chunks replay a fixed append-only
window and use deterministic byte boundaries. Rewriting or deleting a document changes neither the
old page's bytes nor its `ETag`. Replaying a cached snapshot and resuming from `S` delivers every
later write in commit order and converges on the latest state.

Client state is up to date only after a fresh tail response says `up_to_date: true`; immutable pages
cannot vouch for the current head. A changed document that does not match (deleted, or no longer
matching) is always a `delete` op, so clients may receive deletes for ids they never had and ignore
them. Replication before-images could remove those redundant deletes later.

## Body

```json
{"shape_id":"9f2c…","handle":"h1-…","offset":"120","next_offset":"133","up_to_date":false,
 "changes":[{"offset":"125","ops":[
   {"op":"upsert","id":"0192…","version":"tbv1-…","document":{"title":"Hi","read":false}},
   {"op":"delete","id":"0193…"}]}]}
```

- `offset` echoes the request. `up_to_date` is `true` only on an open tail or live response whose
  `next_offset` is the head. Immutable snapshot pages and closed chunks never say it: a client is
  up to date only after a fresh read of the log.
- `version` is `tbv1-` plus the first 16 bytes of SHA-256 over the database scope (derived from its ids, the same on every member),
  collection, document id, merge version and deletion bit, separated by NUL bytes, followed by a NUL
  and the raw UTF-8 document bytes (empty for a tombstone). An equal-version body winner or deletion
  changes this validator. Existing cached tokens must be refreshed after this change. Recreating or copying a
  database changes its ids, so an old ETag cannot authorize a write or produce a false 304
  even when the new database reuses the same offset and id.
- `shape_id`: 32 lowercase hex characters, the first 16 bytes of
  SHA-256(`"TB-SHAPE-V1\0" database \0 collection \0 canonical filter \0 database incarnation \0 collection generation`).
- `handle`: `"h1-"` and the first 8 bytes in hex of SHA-256(`"TB-SHAPE-HANDLE-V1\0"` + the same fields).
- The canonical filter is compact JSON of `where` (whitespace removed, member order kept), with
  relaxed escaping so HTML characters remain literal; empty when absent. The `307` `Location`
  carries the canonical `where`, handle and first snapshot offset, so clients converge on identical
  URLs and cache entries. Changing that encoding rotates handles for affected filters.
- Some Unicode characters still require JSON escaping. Before admitting a read, the endpoint
  checks the complete percent-encoded continuation target, including the longest snapshot offset
  and handle, against the 8 KiB request-line ceiling with 64 bytes reserved for protocol framing.
  Oversized targets return `414 uri_too_long` with `no-store` before any successful redirect.

## Headers

| Response | `Cache-Control` |
| --- | --- |
| Successful snapshot, log, live, `304`, `307` | `private, max-age=0, must-revalidate` |
| Errors, including `409` | `no-store` |

| Header | On | Value |
| --- | --- | --- |
| `Vary` | `200`, `304`, `307` | `Authorization` |
| `ETag` | `200`, `304` | `"<shape_id>:<handle>:<offset>:<next_offset>:<digest>"`, digest = first 8 bytes in hex of SHA-256 over the body; `If-None-Match` with it returns `304` |
| `TB-Shape-Handle` | `200`, `304`, `307`, `409` | current handle |
| `TB-Shape-Next-Offset` | `200`, `304` | `next_offset` |
| `TB-Shape-Up-To-Date` | `200`, `304` | `true` / `false` |
| `TB-Shape-Cursor` | live | `max(floor(unix_seconds / 20), input cursor + 1)` |

Conditional reads use the same strict `If-None-Match` list parser as document reads. Matching
weak tags and a bare `*` return `304`; malformed or partially valid lists return
`400 invalid_request` with `no-store`. A quoted `"*"` is an ordinary tag.

## Live mode and request collapsing

- `live=true` with `O` behind the head returns the chunk at once.
- At the head it waits up to 20 s for any write commit of the database, then reads again. Every
  commit, collection creations and drops included, advances the offset. The store also counts write
  commits (`IDatabaseStore.CommitSequence`); the read takes the count before its snapshot and
  `WaitForCommitAsync` waits for it to grow, so a commit racing the read is never missed.
- On timeout it returns `200` with no changes, `next_offset = O` and `up_to_date: true`.

`ShapeService` keeps one in-flight read (a flight) per (scope, handle, offset, live), where the scope
is the database, collection and canonical filter. Concurrent identical requests, live waiters
included, share it: N pollers cost one storage read before the wait and one after the commit. The
flight runs on its own token, not a requester's:

- A disconnecting client cancels only its own wait; the last requester to leave cancels the flight.
- Every flight is cancelled by application stopping.
- A live flight has the 20 s deadline, counted from when it started; a request that joins late waits
  less than 20 s. Every wake and the final non-waiting read on timeout reopen the database and
  revalidate its incarnation and the handle. A writer still finishing on a deleted file may wake
  that old store; it cannot make the replacement database accept the old handle.
- Each live response advances its cursor beyond the supplied cursor, including early wake-ups,
  late requesters sharing a flight, and a clock moving backwards. A client therefore always has a
  new live cache key. Without an input cursor, the response uses the current time bucket. The
  maximum signed 64-bit cursor is rejected because it has no representable successor.

The service admits at most 32 independently running flights and 256 joined requesters. A new
flight beyond that limit returns `429 shape_busy` with `no-store`; joining an existing flight at
32 is allowed until the requester limit is reached. There is no admission queue. An abandoned
flight retains its running slot until its work finishes, so cancellation cannot evade the limit;
completion and failure also release the slot. Each serialized body remains bounded by 8 MiB.
These fixed caps bound concurrent shape work; charging it alongside other features through the
shared memory budget remains deferred (`ponytail:`).

Metrics on the `TerraScale.TerraBase` meter, without tenant labels:

| Instrument | Kind | Meaning |
| --- | --- | --- |
| `terrabase.shape.reads` | counter | storage reads after collapsing |
| `terrabase.shape.requests.waiting` | up-down counter | requests waiting on a shared read |
| `terrabase.shape.flights.active` | up-down counter | admitted running flights, including cancellation cleanup |
| `terrabase.shape.live.waits` | up-down counter | live flights waiting at the head for a commit (after their read) |

## Must-refetch

`409` with `{"error":{"code":"must_refetch","message":"…","handle":"<current>"}}` and
`TB-Shape-Handle`. It is triggered by:

- A stale handle, for example after the collection was dropped and recreated (new generation,
  new handle). This also applies to `offset=-1` when a handle is sent.
- An offset below the history floor or past the head (`S` or `O`).

A dropped collection that was not recreated is `404 unknown_collection`. The client restarts at
`offset=-1` without a handle.

## Tenancy and CDN cache keys

The endpoint resolves the request's `TenantContext` like every data route: the route's database,
inside the signed project and its verified storage namespace. Unverified legacy project mappings
do not grant JWT access (TS-589, decision 0011). A hidden legacy key instead selects its organization
and reaches only its granted project and database. A database that does not exist and one the
credential does not reach both answer `404 unknown_database`, checked before the query string,
so a credential learns nothing about other tenants.
Everything that names a shape goes through `ShapeRequest.Scope` (organization, project and database
ids, collection, canonical filter), which keys `shape_id`, `handle` and request collapsing, so no two
tenants share a shape id, a handle or a collapsed read. The project and database are in the URL path.

A database deleted and created again under the same name is a new database with a new id. A shape
read of the deleted one, including a live poll woken by the change, answers `404 unknown_database`;
a restart at `-1` reaches the new database under a new handle.

Origin responses are `private, max-age=0, must-revalidate`: shared caches must not store them,
and private caches must revalidate before reuse. JWT signature, issuer, audience, lifetime and
project/database scope are checked on each request, including conditional reads of immutable
snapshots. Deleted legacy keys return `401`; legacy keys without `shapes:read` return `403`.
`Vary: Authorization` and credential-specific cache keys do not detect revocation or scope changes.

**Deployment gate**: shared CDN caching is unavailable with the current origin headers. A future
trusted edge may assign shared cache lifetimes, including one year for immutable pages, only after
checking current authorization before every cache lookup. No such Worker is implemented here.
Rules for that future edge:

1. The cache key keeps the full path and the full query string (`where`, `handle`, `offset`, `live`,
   `cursor`). Never strip parameters or ignore them.
2. Current authorization happens before every cache lookup, including a hit. It must recognize
   revoked credentials and removed scopes; a cache entry keyed by a credential alone is insufficient.
3. Never cache `401`, `403`, `404` or `409`. The origin sends `no-store` on the errors it produces;
   an exception thrown after an endpoint prepared a success response clears that response first.
4. A standards-compliant shared cache must honour `Vary: Authorization`.

## Divergences from shapes-v1.md

Requested by TS-474; recorded here as the decision.

1. Shapes are addressed by collection and `where` on the collection route, not created through the
   `POST …/shapes` gatekeeper and read at `/v1/shapes/{shape_id}`.
2. Since TS-589, public authorization uses ts-auth project access JWTs with `shapes:read`,
   validated against the configured issuer's JWKS. Legacy API keys remain a hidden compatibility
   path. There are no separate data-key `shape` grants or edge shape tokens.
3. No projection, no server-sent events and no edge Worker.
4. Current-generation history is never reaped. Dropped generations are removed atomically because
   their handles already require refetch. A migration baseline floor rejects offsets from before
   reconstructable history; there is no time-based retention expiry for a live generation.
5. The snapshot `P` is a keyset, not a page index.
6. No conflict siblings: a single writer per database never has them (0003).
7. Body size: a commit is sent whole in one chunk. Every storage transaction admits at most 1024
   document writes, including repeated writes and tombstones, and 7 MiB total including document
   bytes, escaped ids and operation framing. Individual documents are limited to 1 MiB and engine
   ids to 1024 UTF-8 bytes. The remaining 1 MiB covers the outer response framing, keeping the
   contract's 8 MiB ceiling. A rejected storage write prevents committing partial work. HTTP writes
   also enforce their document budget and report `413 payload_too_large` with the failing op.
   `api-v1.md` has no such commit limit; patches could otherwise grow a 4 MiB request into 1 GiB.
8. Bodies carry each document's stored bytes unchanged (storage writes refuse invalid UTF-8, so
   no replacement character grows a document after admission). The `ETag` adds a digest of those bytes;
   immutable snapshots and closed chunks retain it after writes. A singleton snapshot document
   may exceed the usual 1 MiB target once framing is included, bounded by the 8 MiB body ceiling.
9. `up_to_date` is never `true` on a snapshot page or closed chunk (the contract sets it whenever
   `next_offset` is the head). Those responses describe immutable historical state, so only a fresh
   open tail or live response can vouch for the current head and end a sync.
10. Origin shape responses require private cache revalidation instead of public shared cache
    lifetimes. Shared caching must wait for an edge component that checks current authorization
    before every hit; `Vary` alone cannot enforce API-key revocation or scope changes.

## Read-your-writes

Writes return `offset`. A client that needs its own write waits until a received `next_offset` is at
least that offset.

## Shared query continuation bounds

Document queries retain stateless keyset cursors. Large positions use a versioned string table:
each distinct sort string is stored once as UTF-8/base64url, with validated references from the
rank/value pairs. This avoids repeated-value and JSON-escaping inflation; the token stays below
2 MiB and decoding rejects oversized positions and invalid UTF-8. Small legacy cursors still decode.
Before issuing a continuation, queries also reserve its bytes alongside the normalized filter,
sort, projection and request framing within the existing 4 MiB request allowance. A combination
that cannot fit returns `413 payload_too_large` on the originating request rather than issuing a
cursor that cannot be resumed.

## Out of scope

- Client SDKs.
- In-memory caches of pages or chunks at the origin; caching is left to HTTP caches.
