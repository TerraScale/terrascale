# Shape reads, protocol v1

> OCI adaptation of the upstream proposed contract pinned in `pin.json`; hosting wording is updated, wire semantics are unchanged. Production runs in OCI gru under ts-orch, with GHCR images and OCI Vault secrets delivered via instance principal. This is not deployment qualification.

- Status: **proposed contract, not implemented.** Shape endpoints are not on `main`. Each rule names the lane that builds it
  (FR-xx, B1, A3, A4; see [free-reads.md](free-reads.md)).
- Conformance: [shape-vectors.json](shape-vectors.json) (section 16). Where this text and a vector disagree, both are
  wrong until fixed together.
- Predecessor: TS-474 shape reads (`7842731a`, removed by #77). Rules kept from it are marked **(TS-474)**. Its
  `change_log`, commit-clock offsets and edge-authorization gate are not part of v1.

## 1. Contract

A shape is one collection plus an optional filter. It syncs as a sealed snapshot, then log chunks, then a live
tail. Every response is addressed by `(handle, offset)`.

1. **Lineage.** A handle names one machine's feed of one collection incarnation (section 5). Offsets are positions in
   that machine's `_tb_feed.seq` (section 3). Two machines never share a handle.
2. **Immutability.** Snapshot pages and closed chunks are sealed objects. Their bytes are fixed when they are sealed,
   including `cursor_resume` and `values_at`. They are never recomputed.
3. **Atomicity.** All of one commit's operations on the collection arrive in one change, in one response. Commits are
   never split.
4. **Values at seal time.** An operation carries the document as materialized at `values_at`. That value is never
   older than the event and can be newer. Clients converge by applying changes in order.
5. **Freshness.** Only fresh responses (section 4) can say `up_to_date: true`. Sealed objects never do.
6. **Durable-only publication.** A response is served only when the durable frontier covers both its `covered_offset`
   and its values (section 10).
7. **Fail closed.** A resume or continuation that cannot be proven complete answers `409 must_refetch`.

Nothing is promised across collections, across handles (beyond section 9), or about read-your-writes (section 15).

## 2. Route and validation

`GET /v1/projects/{project}/databases/{database}/collections/{collection}/shape?where=&handle=&offset=&live=&cursor=`

Served on the client listener (TLS) and built on `TerraScale.Endpoints`. The bearer token must grant
`documents:read`, and its project and database claims must match the path. OpenAPI tag `Live`.

| Parameter | Value |
| --- | --- |
| `where` | Optional filter (below). Absent means every document. |
| `offset` | Required: `-1`, `snap.<S>.<P>`, or a decimal commit boundary `O` (section 3). |
| `handle` | Required unless `offset=-1`. Bootstrap with a handle is `409`; a cursor query with a handle is `400`. |
| `live` | `true`, only with a decimal `O`. |
| `cursor` | With `live=true`: the decimal live bucket (section 4). With `offset=-1`, no `handle` and no `live`: an opaque resume cursor (section 9). Any other use is `400`. |

Header `TB-Shape-Resume: <opaque>` carries the same resume cursor when the request target would not fit (an R8
cursor reaches about 19 KiB). The server reads it only when `offset=-1` and no `handle` is sent; on every other
request it ignores the header, including after a client follows a same-origin `307` and preserves custom headers.
A resume request with `live` or both the header and `cursor` is `400 invalid_request`. SDKs pass the token byte for
byte and never parse it. They use the query form when the full target fits and the header otherwise.

**Filter.** `where` is a JSON filter in the grammar and limits of the `filter` member of `POST …/query`
(`QueryDocumentsEndpoint`): `{"and"|"or":[F…]}` with 1 to 64 children, `{"not":F}`,
`{"eq"|"ne"|"lt"|"lte"|"gt"|"gte":[P,V]}`, `{"in"|"nin":[P,[V…]]}` with 1 to 256 values, `{"exists":P}`,
`{"prefix":[P,"str"]}`, `{"contains":[P,V]}`. At most 8 levels and 256 nodes. `P` is a JSON Pointer or `"$id"`;
`V` is a JSON scalar. There is no sort, limit, cursor, projection or count. Both the raw `where` string (after URL
decoding) and its canonical filter must be at most 8 KiB of UTF-8, and both must satisfy the query v1 grammar and
its depth/node limits. Validate the input before normalization; normalization does not admit an invalid input.
Absent `where` has the empty canonical string; a present empty string is `400 invalid_request`, and JSON `null`
is `400 invalid_query`. **(TS-474; canonicalization updated by FR-15)**

**Canonical filter (FR-15).** Normalize the parsed query v1 filter recursively, then emit compact UTF-8 JSON with
`Utf8JsonWriter` and `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (so `<` stays literal). These are the identity rules:

1. For `and` and `or`, normalize children, flatten children with the same operator, sort by each child's emitted
   canonical UTF-8 bytes (unsigned lexicographic order), and remove byte-identical duplicates. A single remaining
   child replaces the group. Input child order and same-operator nesting do not affect identity.
2. If flattening leaves `n > 64` children, emit `g = ceil((n - 64) / 63)` additional groups of the same operator.
   Order children by descending emitted subtree depth (a leaf has depth 1), breaking ties by emitted UTF-8 bytes.
   Keep the first `64 - g` children at the root; partition the remainder into consecutive groups of at most 64.
   Sort each new group's children by emitted bytes, then sort the root children, including the new groups, by
   emitted bytes. This deterministic regrouping keeps every group within 64 children and preserves the grammar's
   depth and node bounds; revalidate the emitted filter under the same query limits.
3. For `in` and `nin`, normalize scalar values, sort by each value's emitted canonical UTF-8 bytes, and remove
   byte-identical duplicates. One remaining value emits `eq` or `ne`, respectively. Treat `ne` as `not(eq)` and
   `nin` as `not(in)` during normalization; cancel double `not`, and emit `not(eq)` as `ne` and `not(in)` as `nin`.
   Other `not` expressions retain `not`.
4. Preserve literal JSON types and parsed values. Parse exact signed 64-bit integers without converting them to
   double, including `9007199254740993`; other accepted numbers use the query parser's finite binary64 value.
   A whole binary64 value in `[-2^63, 2^63)` emits as an integer (`1.0` → `1`, `-0` → `0`), including values in
   `contains`. Strings, booleans and null remain distinct from numbers. Numeric token spelling is not preserved.
5. Re-emit paths from their decoded JSON Pointer segments, escaping `~` as `~0` and `/` as `~1`; `$id` stays `$id`.
   JSON string escaping follows the compact relaxed writer above. Whitespace and original JSON escaping are not
   preserved.

Only the normalization rules above define equivalent filter spellings for identity. They do not promise that every
pair of predicates with the same results has the same canonical filter (for example, no distributive or De Morgan
rewrites). Member order is not an identity input: each valid filter object has exactly one operator. The canonical
filter is itself a valid `where` and canonicalizing it again yields the same bytes.

Validation, `400 invalid_request` unless noted **(TS-474)**:

- Only the parameters above, each at most once and non-empty.
- Decimals are canonical: no sign, no leading zeros, at most `9223372036854775807`. `P` is `0` or the canonical
  unpadded base64url of a document id's UTF-8 bytes.
- A `where` that is not JSON or not a valid filter, or whose raw or canonical UTF-8 exceeds 8 KiB, is
  `400 invalid_query`. So is a filter over the query budget (5 million SQLite VM instructions or 5 seconds).
- A resume envelope that does not decode canonically, or whose inner R8 cursor fails `ChangeCursor.Decode`, is
  `400 invalid_request`.
- Unknown database, or one the credential cannot reach: `404 unknown_database`. Unknown collection:
  `404 unknown_collection`. Both are checked before the query string.
- Each continuation target, percent-encoded with the longest snapshot offset and handle, must fit the 8 KiB request
  line with 64 bytes reserved. Otherwise `414 uri_too_long`, before any redirect.

## 3. Offsets, snapshots and chunks

| Offset | Meaning |
| --- | --- |
| `-1` | `307` to `snap.<S>.0` of the newest published snapshot. With a resume cursor: section 9. |
| `snap.<S>.<P>` | Snapshot page of snapshot `S` after document `P` (`0` = first page). |
| `O` | Every feed row with `seq ≤ O` was delivered. `O` is always a commit boundary. |

`Location` carries `where` (canonical, when present), then `handle`, then `offset`, so clients converge on identical
URLs.

### 3.1 Feed sequence

- `seq` is a new `_tb_feed` column allocated from a persisted counter row in the same transaction as the feed insert
  (B1). It is never `rowid` or `AUTOINCREMENT`: on Turso 0.8.2 an emptied table restarts at 1 after reopen
  (`turso-spike.md`), and retention can empty the feed.
- The counter is per database file, shared by every collection, positive, and never reused within an incarnation.
- One commit's rows get consecutive `seq` values. The commit's last value is its `commit_seq`. A **commit boundary**
  is any `commit_seq`, or `0`.
- `seq` is not `commit_offset` (the write response's `offset`) and not an HLC stamp. Section 15 explains why they
  cannot be compared.
- The **sealed head** is the largest commit boundary at or below the durable frontier `F` (section 10).

### 3.2 Snapshots

- A snapshot `S` holds the collection's live documents matching the filter. Every page of `S` is built in **one read
  transaction** whose feed head is `S`, so bodies are frozen at `S`. Its `values_at` is `S`. Past states are never
  reconstructed from current rows.
- Pages hold up to 1000 documents or 1 MiB of body, in ordinal id order. A page examines at most 10000 ids within a
  5 s deadline. It may have no ops and still advance. A single larger document is an allowed singleton page, bounded by
  8 MiB. **(TS-474)**
- `next_offset` is `snap.<S>.<base64url(last examined id)>`. The last page's `next_offset` is the decimal `S`.
- A new snapshot is sealed when 4096 chunks have been sealed since the last one or an hour has passed (constants).
  Older snapshots stay servable until retention removes them.
- `-1` redirects to the newest snapshot that is published (`F ≥ S`). If the handle has none, the request seals one at
  the current feed head. When `F` does not reach that `S` within the request deadline, it answers `503 unavailable`
  with `no-store`.
- A `snap.<S>.<P>` that names no sealed page of this handle answers `409 must_refetch`.

### 3.3 Chunks

- A request at decimal `O` below the sealed head returns the **closed chunk** that starts at `O`. If none is sealed
  yet, the sealer seals it now. From then on that URL always returns those bytes.
- A closed chunk covers a non-empty, commit-boundary prefix of `(O, sealed head]`. It holds at most 4096 feed rows and
  stops before the commit that would take the body past 1 MiB. A single larger commit travels alone, bounded by
  8 MiB. Within those bounds, the sealer may close at any commit boundary. Its `next_offset` is the boundary where it
  stopped.
- Commits with no row in this collection are omitted, but `next_offset` still advances past them. A commit whose
  documents no longer match the filter appears as `delete` ops.
- Each change is one commit: `offset` is its `commit_seq`. It lists each document the commit touched once, in ordinal
  id order, with the value at `values_at`.
- **Oversized commit.** If one commit's materialized change exceeds 8 MiB, the read answers `503 shape_too_large`
  with `no-store`. Nothing is sealed and no offset advances. The client keeps its state and offset and retries with
  backoff. Section 15 covers the escape path.
- A decimal `O` that is not a commit boundary, is below the retention floor, or is above the sealed head answers
  `409 must_refetch`.

Chunk sealing runs with change capture suspended and refuses damaged rows as #152 does (FR-12). Sealed bytes are
derived data: not shipped, not backed up, dropped with their handle, and kept for 7 days (FR-16).

## 4. Fresh responses, live mode and collapsing

A **fresh** response is built per flight and never persisted or mirrored. Fresh responses are:

- a non-live request at `O` = sealed head: no changes, `next_offset = O`, `up_to_date: true`;
- every live response at the sealed head.

A live request behind the sealed head returns the closed chunk at once **(TS-474)**.

- At the head, a live request waits up to 20 s for a commit. The read takes the store's commit count before its
  snapshot, and `WaitForCommitAsync` waits for it to grow, so a racing commit is never missed **(TS-474)**.
- **Seal-on-wake:** a woken flight asks the sealer to advance up to the committed head, then answers. A fresh body
  covers `(O, next_offset]` under the chunk bounds of section 3.3. `up_to_date` is `true` only when `next_offset` is
  the sealed head. It carries a fresh `values_at` and a fresh `cursor_resume`.
- On timeout the body is `200`, with no changes, `next_offset = O` and `up_to_date: true`.
- `TB-Shape-Cursor` = `max(floor(unix_seconds / 20), input cursor + 1)`, or the current bucket without an input. An
  input of `9223372036854775807` is `400 invalid_request` **(TS-474)**.
- One flight per `(handle, offset, live, cursor)`. At most 32 running flights and 256 joined requests. A new flight
  beyond 32 answers `429 shape_busy` with `no-store`. There is no queue. An abandoned flight keeps its slot until its
  work ends. A disconnecting client cancels only its own wait. The last requester to leave cancels the flight.
  Application stop cancels every flight **(TS-474)**.
- Every seal, page, fresh body and flight reserves memory through `IMemoryBudget` (FR-17).

## 5. Identity

Inputs, all UTF-8, in this order:

| # | Field | Value |
| --- | --- | --- |
| 1 | `organization_id` | Tenant organization, canonical uppercase ULID |
| 2 | `project_id` | Project id |
| 3 | `database_id` | Database id |
| 4 | `incarnation` | `IDatabaseStore.Incarnation`, 26-character ULID, drawn on every create, copy, restore and rebuild |
| 5 | `replica_id` | Serving machine's `ReplicaId.Value`, decimal uint64 |
| 6 | `replica_epoch` | That `ReplicaId`'s epoch (its low 16 bits), decimal |
| 7 | `collection` | Collection name |
| 8 | `collection_generation` | `collections.entity_id`, 26 characters; it changes when a collection is re-created |
| 9 | `canonical_filter` | Section 2; empty when absent |
| 10 | `wire_format` | `1` |

`fields` = the ten values joined by single NUL bytes, with no trailing NUL.

- `shape_id` = lowercase hex of the first **16** bytes of `SHA-256("TB-SHAPE-V1" ‖ 0x00 ‖ fields)` (32 characters).
- `handle` = `"h1-"` ‖ lowercase hex of the first **8** bytes of `SHA-256("TB-SHAPE-HANDLE-V1" ‖ 0x00 ‖ fields)`
  (16 characters).

`wire_format` names this body and TBS1 contract. It is not `StorageFormat.Id`.

Any input change rotates both values: database copy, restore or re-create; collection drop or re-create; a machine
change or unclean start (new `replica_epoch`); a filter-encoding change. **Handles are per machine, not per region.**
Regional routing can send two requests from one client to different machines in the same region. A handle unknown to the
receiving machine answers `409 must_refetch`; the client may make one resume attempt (section 7, rule 6).

## 6. Body

```json
{"shape_id":"…","handle":"h1-…","offset":"2","next_offset":"4","covered_offset":"4","values_at":"4","up_to_date":false,
 "cursor_resume":"AQAA…",
 "changes":[{"offset":"4","ops":[
   {"op":"upsert","id":"01ARZ3NDEKTSV4RRFFQ69G5FB0","version":"tbv1-…","document":{"title":"Updated","read":true}},
   {"op":"delete","id":"01ARZ3NDEKTSV4RRFFQ69G5FB1"}]}]}
```

- UTF-8, no BOM, compact, members in the order shown. Document bytes are the stored bytes, unchanged. These exact
  bytes are hashed for the `ETag` and sealed by TBS1. Clients never re-serialize them for comparison.
- `offset` echoes the request. `offset` and `next_offset` use section 3's grammar. `covered_offset`, `values_at` and stamps are decimal strings.
- `covered_offset`: the decimal offset this body covers. Chunks and fresh bodies: `next_offset`. Every snapshot page:
  `S`. The publication gate uses it (section 10).
- `values_at`: the feed head of the read transaction that materialized the values (`S` for snapshots).
- `cursor_resume`: section 9.
- `version` is the Document API v1 version of the materialized row: `ETags.Version(scope, collection, id, stamp,
  body)`. That is `tbv1-` plus the first 16 bytes of SHA-256 over `scope` (`IStorageTransaction.Scope`, Guid format
  `N`) ‖ NUL ‖ `collection` ‖ NUL ‖ `id` ‖ NUL ‖ decimal row stamp ‖ NUL ‖ `0` ‖ NUL, followed by the raw body. It is
  the same token a document read returns, so SDKs may pass it to conditional writes. Clients treat it as opaque.
- `delete` carries no version. It is sent when the document is absent at `values_at` or no longer matches the
  filter. Clients may receive deletes for ids they never had.
- Snapshot pages hold one change at `offset = S` with only `upsert` ops. An empty snapshot is one page with
  `ops: []`.

## 7. Client apply rules

1. Apply each change atomically, in order. Upsert replaces the document. Delete removes it; an unknown id is a no-op.
2. After the whole response is applied, persist `handle`, `next_offset` and `cursor_resume` atomically with the
   state.
3. A `-1` bootstrap without a resume cursor starts from empty state. After a proven resume redirect (section 9), keep
   the state and apply the new lineage's operations in order.
4. Never deduplicate by event identity `(origin, stamp, collection, id)`. A repeated event can carry a newer
   materialized value, and skipping it loses that value.
5. A snapshot's `cursor_resume` is usable only after **every** page of that snapshot is applied. An interrupted
   snapshot either continues paging the same snapshot on the same handle, or restarts at `-1`. It never resumes on
   another lineage from a page's cursor.
6. On a continuation `409 must_refetch` whose `TB-Shape-Handle` differs from the saved handle, a client with a
   usable `cursor_resume` (rule 5) may make **one** resume attempt. The attempt is the `offset=-1` cursor request and
   the single `307` follow-up it returns. Any `409` on that attempt, any other `409`, or no usable cursor: discard
   state and restart at `-1` without a handle or resume token. Never start a second attempt against the same saved
   state. `503 shape_too_large`, `503 unavailable`, `429 shape_busy`: keep state and offset, retry with backoff.

## 8. Must-refetch

`409`, `Cache-Control: no-store`, header `TB-Shape-Handle`, body
`{"error":{"code":"must_refetch","message":"…","handle":"<current>"}}`. Raised for:

- a stale or unknown handle, including `offset=-1` sent with a handle;
- an offset below the retention floor (7 days), above the sealed head, not a commit boundary, or naming no sealed
  snapshot page;
- a resume that is unproven, expired, mismatched or behind (section 9);
- filtered-shape eviction (at most 64 filtered shapes per database, FR-15).

With `offset=-1`, a handle plus a `cursor` query is `400 invalid_request`. A handle without that query is
`409 must_refetch`; `TB-Shape-Resume` is ignored when a handle is present (section 2).

A dropped collection that was not re-created is `404 unknown_collection` **(TS-474)**.

## 9. Resume across machines

Every body carries `cursor_resume`, an opaque envelope: `base64url(0x01 ‖ resume_scope16 ‖ R8)`. `R8` is the R8 changes
cursor (`ChangeCursor`, #207) that a feed read with `collections=<this collection>` would issue after delivering exactly
the rows with `seq ≤ covered_offset`.

- `resume_scope16` = first 16 bytes of `SHA-256("TB-SHAPE-RESUME-V1" ‖ 0x00 ‖ fields)`, where `fields` = organization,
  project, database, collection, collection generation, canonical filter and `wire_format`, joined by NUL with no trailing
  NUL. It excludes incarnation, replica id and replica epoch, so it is the same on every machine.
- Rows the `where` filter drops still advance it, so the proof chain stays complete.
- Sealed objects freeze it at seal time; its issue stamp is the seal stamp. Fresh responses compute a new one.
- Layout (for vectors only; clients never parse it): the envelope is `0x01`, then the 16 scope bytes, then `R8`. `R8` is
  marker `0x01`, issue stamp, issuer `ReplicaId`, then per origin in ascending order the origin, stamp, retention floor
  and 32-byte prefix hash. Integers are 8-byte big-endian; the token is unpadded base64url; at most 256 origins.

A resume is `offset=-1`, no handle, and the cursor in `cursor` or `TB-Shape-Resume`. The receiving machine:

0. Decodes the envelope and compares `resume_scope16` with its own. A malformed envelope or `R8` is `400 invalid_request`.
   A scope mismatch (another organization, project, database, collection, collection generation, filter or wire format)
   is `409 must_refetch`.
1. Checks `R8` under the R8 rules. Cases where R8 answers `410 cursor_expired` answer `409 must_refetch` here: retention
   deleted a promised event, the cursor is older than 7 days, or the prefix cannot be verified.
2. Answers `409 must_refetch` when it is **behind**: its highest stamp for any named origin is below the cursor's.
3. Marks a local feed row **covered** when the cursor names its origin and its stamp is at or below that origin's
   stamp.
4. Computes the target as the earlier of two offsets: the commit boundary just before the first uncovered local row's
   commit, and the local sealed head. With no uncovered row, the target is the local sealed head. It answers `307` to the
   local handle at that target. A target below this machine's retention floor answers `409 must_refetch`.

Delivery is at least once: covered rows after the target are delivered again, with destination values (section 7,
rule 4). Never skip on a stamp comparison alone.

**Planned gate.** Rules 1 and 2 need proofs persisted and resumes replayed to the issuing machine (#211, open). FR-23
lands after #211. There is no runtime switch. Until then, every resume answers `409 must_refetch`.

## 10. Durability and publication

- **Durable frontier `F`.**
  - Before FR-25, `F` is the committed feed head. Every write transaction syncs its WAL at commit (#181), so a seal
    needs no extra barrier (D4).
  - After FR-25, `F` is the largest `seq` such that **every** commit with `commit_seq ≤ F` has its change group
    remotely acknowledged under the current incarnation. It is a contiguous prefix, not the highest isolated
    acknowledged row.
  - Acknowledgement comes from A3 (remote ack wait) and A4 (incarnation-scoped acks). The mapping from
    `CommitMarker(Incarnation, ChangeId)` to `seq` belongs to FR-25.
- **Gate.** A sealed object or fresh body is served only when `F ≥ max(covered_offset, values_at)`. Its values may be
  newer than its range, so it waits until those newer values are durable too. The gate is evaluated on every serve.
  An object sealed before `F` caught up stays unpublished until then. During a partition the gate may never clear;
  `-1` keeps answering the newest published snapshot, and reads above `F` wait or answer `503 unavailable`.
- **No new mechanism.** There is no copy-on-overwrite, no pre-image table, and no reconstruction of past values.
- **Limits today.** Writes report `local_durable` (`DocumentRoute.Durability`). Until FR-25, a published object can
  contain a write that a later host or volume loss erases. That write then stays in cached copies and client state.
  Whether Turso 0.8.2 FULL survives power loss for tenant databases is unproven until B3.

## 11. Headers, ETag and transport tiers

| Response | `Cache-Control` |
| --- | --- |
| Sealed object, fresh body, `304`, bootstrap `307` (origin) | `private, max-age=0, must-revalidate`, with `Vary: Authorization` |
| Resume `307` | `no-store` |
| Errors (`400`, `404`, `409`, `414`, `429`, `503`) | `no-store` |
| Keys and manifest | `private, no-store` |
| TBS1 objects in Tigris | `public, max-age=31536000, immutable` (FR-24, after A1 and FR-21) |

| Header | On | Value |
| --- | --- | --- |
| `ETag` | `200`, `304` | `"<shape_id>:<handle>:<offset>:<next_offset>:<digest>"`, digest = hex of the first 8 bytes of SHA-256 over the body bytes |
| `TB-Shape-Handle` | `200`, `304`, `307`, `409` | Current handle |
| `TB-Shape-Next-Offset` | `200`, `304` | `next_offset` |
| `TB-Shape-Up-To-Date` | `200`, `304` | `true` / `false` |
| `TB-Shape-Cursor` | live | Section 4 |

`If-None-Match` uses the strict list parser of document reads. Matching weak tags and a bare `*` give `304`. A
malformed list is `400 invalid_request`. A quoted `"*"` is an ordinary tag **(TS-474)**. Authorization is checked on
every request, including conditional reads.

Tiers (D1):

- **T0, origin.** Plaintext JSON, private, with flights and `ETag`/`304`. The origin never serves TBS1 bytes and never
  marks a response shared-cacheable. Fresh bodies hold freshness and resume tokens, so they stay private.
- **T1, Tigris.** Only sealed objects, only as TBS1 ciphertext, found through the manifest (section 14).
- No CDN.

## 12. TBS1 sealed bytes

TBS1 seals one sealed object's exact body bytes for Tigris (FR-21, FR-22).

**Keys.** HKDF is HKDF-SHA256 (RFC 5869), output 32 bytes. An empty salt means 32 zero bytes.

1. `K_d`: the project data key named `KID` (26 ASCII characters) in scope `<organization_id>/<project_id>`
   (`DataKeys`, `BackupLayout.KeyScope`).
2. `E = floor(unix_seconds / 600)` on the sealing machine (D3).
3. `K_e = HKDF(ikm = K_d, salt = empty, info = "TBS1 epoch" ‖ 0x00 ‖ handle ‖ 0x00 ‖ decimal(E))`.
4. `binding = handle ‖ 0x00 ‖ request_offset`, where `request_offset` is the canonical offset of the object's URL (for
   example `2` or `snap.0.0`).
5. `K_o = HKDF(ikm = K_e, salt = salt_obj, info = "TBS1 object" ‖ 0x00 ‖ binding)`.

Backup objects derive from the same `K_d` with a random 32-byte salt and a bucket key as info. TBS1's empty-salt
epoch step and `TBS1 ` info prefix keep the two apart.

**Header (70 bytes).**

| Bytes | Field |
| --- | --- |
| 0–3 | Magic `TBS1` (`54 42 53 31`) |
| 4–11 | `E`, uint64 big-endian |
| 12–37 | `KID`, 26 ASCII bytes |
| 38–69 | `salt_obj`, 32 bytes from a CSPRNG, fresh for every object |

**Frames.** Frame `i` (from 0) is a 4-byte big-endian length word, then `n` ciphertext bytes, then a 16-byte GCM tag.
The low 31 bits hold the plaintext length `n`. Bit 31 marks the last frame. There is no stored-length word and no
compression.

- Plaintext is cut into 1 MiB (1048576-byte) pieces. Every non-last frame has `n = 1048576` exactly. The last frame
  has `0 ≤ n < 1048576`.
- A plaintext whose length is a multiple of 1 MiB (including empty) ends with an empty last frame. Exactly 1 MiB is a
  full non-last frame plus an empty last frame.
- Total plaintext is at most 8 MiB (8388608 bytes), so at most 9 frames.

**AES-256-GCM per frame.** Key `K_o`. Nonce (12 bytes) = 4 zero bytes ‖ `i` as uint64 big-endian. Associated data =
the 70-byte header ‖ `i` as uint64 big-endian ‖ the length word.

Every object has its own random salt, so every `K_o` is distinct and a (key, nonce) pair never repeats. Objects are
sealed once. Production never uses a deterministic salt; the vectors fix salts only to stay reproducible.

**Decryption.** The client derives `binding` from its own authorized request (handle and requested offset), never
from the object. A mismatched object fails its tags. Reject the whole object, applying nothing, when:

- the header is short, the magic differs, or `E` or `KID` differ from what the client expected;
- any frame has `n > 1048576`, a non-last frame has `n ≠ 1048576`, or a last frame has `n = 1048576`;
- the total exceeds 8 MiB;
- the object ends before a last frame, or has bytes after it;
- any tag fails (this covers reordered, swapped or tampered frames and a wrong key, handle, offset or epoch).

## 13. Key endpoint

`GET /v1/projects/{project}/databases/{database}/collections/{collection}/shape/keys?handle=&epoch=&key_id=`

- Scope `documents:read`, client listener, same tenancy checks as the shape route. All three parameters are required
  and canonical. Otherwise `400 invalid_request`.
- `epoch` greater than the current `E` is `400 invalid_request`. Future keys are never released.
- Served pairs: the current `E` with the current `KID`, and any `(E, KID)` that a retained TBS1 object of this handle
  names. A retired key that no retained object names is never released. An epoch below retention, or a pair that is
  not served, answers `409 must_refetch`. So does a stale or unknown handle.
- `200` body: `{"handle":"h1-…","epoch":"<E>","key_id":"<KID>","key":"<unpadded base64url K_e>"}`.
- New objects always use the current `E` and `KID`. A cached old object keeps its old epoch. Newly selected data is
  never sealed under an old epoch.

**Revocation bound.** A key is released only to a credential accepted at that moment.

- **API key:** once its revocation reaches the serving machine, no key is released for any epoch.
- **JWT:** TerraBase validates ts-auth JWTs offline, so an upstream-revoked token is accepted until it expires plus the
  allowed clock skew (30 s, `UserAuthenticationHandler.ClockSkew`). Its holder may already hold the current `K_e`, which
  opens objects sealed in the rest of that epoch. The worst case from upstream revocation is: remaining token lifetime +
  30 s + remaining epoch (≤ 10 min).

Whatever the client already read stays readable.

**FR-21 integration.** `IShapeCrypto.EncryptAsync` accepts a tenant, handle, canonical request offset and the exact
sealed plaintext bytes. It returns a disposable `ShapeCiphertext` owning memory admission, with its bytes, epoch and
project data-key id. It always selects the current epoch; callers cannot supply an old epoch or a deterministic salt.
`CurrentEpoch` and `CurrentKeyAsync` expose the current epoch and its handle-scoped key. Keys reuse the platform backup
`DataKeys` and `BackupLayout.KeyScope`; a development host without that key store answers `503 unavailable`.

The key endpoint uses `IShapeLineages` to validate the collection's current handle. Until FR-20 connects filtered
lineage lookup, only the unfiltered lineage is resolvable. Historical `(epoch, key_id)` pairs require
`IShapeKeyRetention.HasObjectAsync` proof from the retained T1 objects; FR-22 supplies that proof. Before T1 objects
exist the default refuses historical pairs with `409 must_refetch`, rather than releasing unproven keys.

## 14. Manifest (presigned, D2)

`GET …/collections/{collection}/shape/manifest?handle=&offset=`, with the same scope, tenancy and errors as section 13.

- Body: `{"handle":"h1-…","objects":[…]}`. At most **8** entries in total. The first entry's `offset` is the
  requested offset; each later entry's `offset` is the previous `next_offset`. The list stops at the first object that
  is unpublished (section 10) or not yet mirrored, so it may be empty. The client then reads that offset from the
  origin.
- Entry: `offset`, `next_offset`, `binding` (JSON string `handle + "\u0000" + offset`), `object`
  (`v1/shapes/<hex SHA-256(shape_id ‖ 0x00 ‖ binding)>`, where `shape_id` is its 32 lowercase hex characters as ASCII and
  `binding` is the UTF-8 string above), `url` (presigned GET), `expires_at`, `epoch`, `key_id`, `sha256` (hex of the
  exact TBS1 bytes) and `size` (bytes, integer).
- `expires_at` is RFC 3339 UTC at the start of the next epoch, `(E_now + 1) × 600`. The presigned URL expires no
  later than that.
- Tigris keys hold no tenant names: only hashes of `shape_id` and binding (the binding carries the handle and offset).
- Client order, applying nothing until every step passes:
  1. Check the offset chain and that `binding` equals the binding derived from the client's own request.
  2. Download the object and check `size` and `sha256`.
  3. Check header `E` and `KID` against `epoch` and `key_id`.
  4. Fetch `K_e` (section 13) and decrypt.
  5. Check that the body's `handle`, `offset` and `next_offset` match.

  Any failure discards that object and falls back to the origin for its offset.
- The mirror never replaces a stored object with different bytes (FR-22).

## 15. Limitations

- **Host loss before FR-25:** section 10.
- **Oversized commits.** One transaction holds at most 1024 changes and 2 MiB of change content, but values at seal
  time can grow each document to 1 MiB. A commit whose materialized change exceeds 8 MiB stays `503 shape_too_large`.
  It clears when its documents shrink, or when a newer snapshot past that commit is published and the client restarts
  at `-1`.
- **No read-your-writes.** Writes return `commit_offset`, a different counter from `seq`. No rule maps one to the
  other yet. A later lane can map `CommitMarker` to `seq`.
- **Resume regressions.** After a cross-machine resume, a document can briefly show the destination's older value.
  It catches up when the destination receives the same events. A source write lost before replication can stay in the
  client for good (host-loss limit above).
- **Per-machine handles:** section 5.

## 16. Conformance vectors

[shape-vectors.json](shape-vectors.json) is self-contained: synthetic keys, tokens, URLs and data. It needs no
TerraBase process. Top level: `format` (`TB-SHAPE-VECTORS-V1`), `protocol_version` (`1`), `description`,
`conventions`, `fixture`, and the vector groups below.

- **Conventions.** Hex is lowercase with no prefix. Binary strings are unpadded base64url unless the field name ends
  `_hex`. Offsets, epochs, replica ids and stamps are decimal strings; byte counts are JSON integers.
- **`fixture`.** The shared scope, its `shape_id` and `handle`, a second machine's (`destination`), the
  `version_scope_guid_n`, `retention_floor`, `committed_head` and `sealed_head`.

| Group | Checks |
| --- | --- |
| `identity_vectors` | Section 5 inputs → `shape_id`, `handle` |
| `filter_vectors` | Section 2 raw `where` → canonical filter and section 5/9 identities, using `fixture.scope` |
| `offset_vectors` | Offset grammar, valid and invalid |
| `cursor_vectors` | Opaque R8 token bytes and verbatim passthrough; live-bucket arithmetic and the max rejection |
| `http_vectors` | Request → status, required header subset, `body` (parsed JSON), `body_utf8` (exact JSON bytes as a string) or `body_hex` (binary bytes), `expected_action`, plus per-vector `fixture` state |
| `apply_vectors` | Initial state + body → expected state and offset (atomic commit, unseen delete, cross-lineage newer value) |
| `durability_vectors` | `F`, `covered_offset`, `values_at` → publish or not |
| `tbs1_vectors` | Positive decryptions with every intermediate: `K_e`, `K_o`, binding, header, per-frame word, nonce, AAD and tag |
| `tbs1_rejection_vectors` | Whole-object rejections (section 12) |
| `tbs1_generated_vectors` | `plaintext_recipe` (`repeat_byte_hex`, `length`): encrypt with fixed keys and salt, compare `ciphertext_size`, `ciphertext_sha256` and frame tags, decrypt, then apply the mutations |

Each `filter_vectors` row has an `id`, `input_where_utf8` (the exact raw JSON string, or JSON `null` for an absent
parameter), `equivalence_group`, and `expected` with `canonical_filter`, `handle`, `shape_id` and `resume_scope_hex`.
The string `"null"` is a present invalid filter, not an absent parameter. Canonicalize the input, replace only
`fixture.scope.canonical_filter` with the result, and derive the identities by sections 5 and 9. Compare canonical
UTF-8 bytes and every expected identity. Rows with the same `equivalence_group` must have equal canonical bytes and
identities; the group labels cover the specified normalization rules, not arbitrary semantic equivalence.

A failed negative vector means the client rejects the complete object or response, applies nothing, and keeps its
offset.

## 17. Out of scope

- SDK shape clients (separate repositories).
- Tier B per-origin delta chunks (D7).
- Projections, server-sent events, edge Workers, CDN caching, and in-memory origin caches of sealed bytes.
