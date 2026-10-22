# Free replication reads for TerraBase (the ElectricSQL trick)

> OCI adaptation of the upstream proposed contract pinned in `pin.json`; hosting wording is updated, wire semantics are unchanged. Production runs in OCI gru under ts-orch, with GHCR images and OCI Vault secrets delivered via instance principal. This is not deployment qualification.

- **Status:** plan, checked against `main` at `519c9ff9` (2026-10-13). Shape reads remain proposed; merged prerequisites are listed in section 2.
  Decisions are in section 7. No CDN (T2) is selected.
- **Protocol:** v1. Handle domain `TB-SHAPE-HANDLE-V1`, handle prefix `h1-`, shape id domain `TB-SHAPE-V1`.
- **Normative contract:** [`shape-reads.md`](shape-reads.md) defines the wire protocol and exact byte layouts.
  [`shape-vectors.json`](shape-vectors.json) holds the conformance vectors (`shape-reads.md` section 16). Where this plan
  and those files differ, those files win.
- **Goal:** N clients that read or follow the same data cost TerraBase about **one storage read per change per cache
  location**, not N. Reads stay authorized, region-local and committed-only until FR-25.
- **Source plan:** `.cos/FREE_REPLICATION_READ.md`, with section 4 decisions in `.cos/replication-improvement-plan.md`.

Permalinks pinned to `f7d5c3c` or `7842731a` are historical. Their line numbers are not current. Repo-relative links
point at `main`'s layout.

---

## 0. Summary

1. **The trick.** Serve every read as an *immutable, offset-addressed chunk* of an append-only log, plus one shared
   long-poll for the tail. An immutable chunk can be cached anywhere, forever. A thousand identical requests then
   become one origin read. This is ElectricSQL's HTTP shape model.
2. **The log already exists.** R8's `_tb_feed` is a per-machine, apply-ordered change index. This plan seals it into
   chunks, adds snapshots and a live tail, and serves them over `/v1`.
3. **Offsets are feed sequence numbers.** A shape offset is `_tb_feed.seq`, from a persisted counter. It is not the
   write path's `commit_offset`.
4. **Shared caches hold ciphertext only.** Sealed objects that leave the origin (T1, Tigris) are encrypted under
   per-object keys (TBS1). Clients fetch the epoch key from an authenticated `no-store` endpoint. A cached or leaked
   object is useless without it.
5. **Transport, in tiers.**
   - T0: the origin, with request collapsing and `ETag`/`304`. Authorized per request, private, plaintext.
   - T1: TBS1 ciphertext mirrored to Tigris, reached through an authenticated presigned manifest.
   - T2: a CDN. **Not selected.**
6. **Committed-only until FR-25.** A chunk is sealed from committed rows, and every write transaction is FULL (#181).
   Until the remote-ack frontier exists (FR-25), a host loss can erase writes that were already published in a chunk.
   The contract says so (3.10). This plan does not claim host-loss durability.
7. **Fix this first:** peer authentication (C1, FR-05). C3 is done (#165), and per-origin stamp order is done (#222).
8. **Work shape:** 30 open items in 7 waves (section 6). Items whose tests take at most 10 minutes go to Claude Haiku
   5.5. The rest go to Claude Opus 5.5 (section 5).

---

## 1. The Electric trick, mapped to TerraBase

| Electric mechanism | Why it makes reads free | TerraBase equivalent in this plan |
| --- | --- | --- |
| A shape is a table plus an optional `where`, synced as a snapshot followed by a log | Clients hold a local replica, so repeated reads cost nothing | A shape is a collection plus an optional filter, synced as a sealed snapshot followed by sealed log chunks (3.4, 3.5) |
| Requests are addressed by `(handle, offset)`; closed chunks never change | A URL maps to fixed bytes, so any cache can keep it with a long lifetime | `handle` binds the serving machine's lineage; `offset` is `_tb_feed.seq` (3.2, 3.3) |
| `live=true` long-poll with a cursor time bucket | Clients at the same offset ask for the same URL in a bucket, so the origin collapses them into one read | 20 s long-poll, cursor buckets and flights, carried over from TS-474 (3.6) |
| A stale handle answers `must-refetch` | Old cache entries never mix lineages | `409 must_refetch` on handle rotation (3.3) |
| A CDN in front, with auth at the edge | The origin serves only cache misses | Encrypt-then-cache. Tigris serves TBS1 ciphertext. No CDN (3.8, 3.9) |

Electric references: <https://electric.ax/docs/api/http> and <https://electric.ax/docs/guides/auth>, as cited by TS-474.

---

## 2. What changed recently, and where reads stand today

### 2.1 Merged since the replication audit

| Change | State | Relevance here |
| --- | --- | --- |
| #152: the sender refuses a damaged CDC row and holds back only that database | merged | Sealing refuses damaged rows the same way |
| #163: a peer's older epochs stop holding retention | merged | — |
| #169: quiet ack ranges; copies and restores reset acks | merged | A copy or restore gets a new incarnation, so a new handle |
| #192: PITR answers 409 unless it can prove the history up to T | merged | The same proof style applies to resume cursors |
| #119: R8 changes-feed E2E | merged | The feed this plan builds on |
| #165: repairs stop appearing under origin 0 (C3) | merged | FR-02 is done. Failover resume no longer answers 400 for repairs |
| #181: every write transaction syncs the WAL at commit | merged ([SqliteDatabaseStore.cs#L226](../../src/TerraBase/Engine/Sqlite/SqliteDatabaseStore.cs#L226)) | A committed row is locally durable on Turso, subject to FR-07 |
| #184: new databases placed in at least two regions | merged | Cross-region resume is the normal path, not an edge case (3.7) |
| #191: own CDC commit marker, `CommitMarker(Incarnation, ChangeId)` ([CommitResult.cs#L6](../../src/TerraBase/Engine/CommitResult.cs#L6)) | merged | FR-25 maps it to a feed sequence (3.4) |
| #221: persistent feed sequence and derived shape storage (B1) | merged | FR-03 is done; `_tb_feed.seq` and the shape tables are persisted ([shape-storage.md](shape-storage.md)) |
| #222: local origin stamps stay increasing across restarts (A2) | merged | FR-01 is done; removes the C7 prerequisite for vector resume |
| #223: shape fan-out benchmark harness and baseline (B2) | merged | FR-06 is done; the baseline is measured against the current changes feed, before shape caching |
| #207: opaque changes cursor with per-origin proofs ([ChangeCursor.cs](../../src/TerraBase/Features/Documents/ChangeCursor.cs)) | merged | Failover resume builds on it (3.7) |

### 2.2 Still open or not wired

| Item | State | Relevance here |
| --- | --- | --- |
| Decision 0013a: success only after two regional durable copies ([0013a](decisions/0013a-remote-durable-acknowledgement.md)) | **proposed** (revision 4). The write wait is implemented on document and transaction routes (`DocumentService`: `AdmitAsync`, then `WaitAsync`); other mutating routes still answer `local_durable` | Gates the frontier move from local to remote (3.4, FR-25) |
| #211 `fix/feed-prefix-persisted` | open | Persisted prefix proofs across restarts, and replay to the issuing machine. Planned gate for resume (3.7) |
| C1 peer RPCs unauthenticated | open | Blocks T1 headers and any shared caching (FR-05) |
| #210 production deployment ([ci.yml](../../.github/workflows/ci.yml)) | merged: the deploy job is enabled again | Production deployment still requires the existing CI and live gates |

### 2.3 Read paths today

- **Point read and query:** authenticated per request, `no-store`, served by the machine the client reaches.
- **R8 changes feed:** `GET /v1/…/databases/{d}/changes` over SSE, scope `documents:read`
  ([GetDatabaseChangesEndpoint.cs#L18](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/TerraBase/Features/Documents/GetDatabaseChangesEndpoint.cs#L18)).
  - Every client holds its own connection. Nothing is cacheable or shared, so **cost grows linearly with readers.**
  - Limits: 32 per database and 1024 per machine, with a 30-minute lifetime
    ([DatabaseChanges.cs#L39-L49](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/TerraBase/Features/Documents/DatabaseChanges.cs#L39-L49)).
  - Each event reads the document's *current* row
    ([SqliteStorageTransaction.Feed.cs#L247](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.Feed.cs#L247)).
- **`_tb_feed` today:** columns `(origin, stamp, collection, id, deleted)` with no stored body
  ([SqliteDatabaseStore.cs#L105-L109](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/TerraBase/Engine/Sqlite/SqliteDatabaseStore.cs#L105-L109)).
  The local order is the implicit `rowid`. Section 3.2 adds `seq`.

### 2.4 Prior art: TS-474 shape reads (deleted in R2c-1, #77)

TS-474 implemented the Electric model: handles, offsets, closed chunks, live buckets, flights and `must_refetch`
([TS474 shape-reads.md](https://github.com/TerraScale/terrascale/blob/7842731ae3e17b009ddc164668d6e5c26bd90c27/docs/terrabase/shape-reads.md),
[TS474 GetShapeEndpoint.cs](https://github.com/TerraScale/terrascale/blob/7842731ae3e17b009ddc164668d6e5c26bd90c27/src/TerraBase/Features/Documents/GetShapeEndpoint.cs)).
It was not free for one reason: its shared-caching gate required "an edge authorization check before every cache
lookup", which was never built
([shape-reads.md#L280-L292](https://github.com/TerraScale/terrascale/blob/7842731ae3e17b009ddc164668d6e5c26bd90c27/docs/terrabase/shape-reads.md#L280-L292)).

| Reused from TS-474 | Changed here |
| --- | --- |
| Route shape, offsets, `307` from `-1`, closed chunks, `409 must_refetch`, live cursor buckets, flights and their limits, metric names | The log is `_tb_feed` (leaderless, per machine), not the deleted `change_log` |
| Validation rules and the URL-length check | Handles bind the serving machine's lineage (3.3) |
| `ETag` and `304` | Sealed bodies are stored once and never recomputed (3.4) |
| — | Encrypt-then-cache replaces the missing edge-auth check (3.9) |
| — | Publication waits for the durable frontier (3.4) |
| — | Region failover resumes through opaque cursor proofs (3.7) |

### 2.5 Open defects that matter for free reads

| ID | Defect on `main` | Why it matters once reads are cached and global | Fix item |
| --- | --- | --- | --- |
| C1 | Peer RPCs are unauthenticated; the network is the trust boundary ([Listeners.cs#L16-L18](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/TerraBase/Hosting/Listeners.cs#L16-L18)) | A forged write would be sealed and cached | FR-05 (blocks FR-24 shared headers) |
| C2 | Acknowledged after the local commit (FULL since #181), with no remote wait | A published chunk can contain a write that a host loss later erases | FR-25; remote ack is item A3 |
| C4 | A replicated collection drop deletes newer writes on the receiver ([Join.cs#L107-L125](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.Join.cs#L107-L125)) | Shape clients see deletes that anti-entropy later undoes. The handle rotates on drop, so clients refetch. The cost is churn, not wrong data | Not in this plan (lane A7) |

---

## 3. Design

### 3.1 Two tiers

- **Tier A, shapes (this plan's main deliverable).** Each machine serves materialized chunks from its own apply order.
  Clients apply `upsert` and `delete` operations; they never need to understand lattices.
- **Tier B, per-origin delta chunks (optional, wave 6).** Each origin's change groups are deterministic and canonically
  encoded, so one copy in Tigris serves every region. This needs the lattice in each SDK and the R7 GC rules settled.

### 3.2 Offsets: `_tb_feed.seq`

- A shape offset is `_tb_feed.seq`. Each feed row takes `seq` from a **persisted feed counter row**, in the same
  transaction that appends the row.
- Not `rowid`, not `AUTOINCREMENT`, not `commit_offset`, not an HLC. On Turso 0.8.2 an emptied `AUTOINCREMENT` table
  restarts at 1 after a reopen ([turso-spike.md#L79](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/docs/terrabase/turso-spike.md#L79)).
  Retention can empty `_tb_feed`, so the counter must live outside it. FR-03 tests this.
- The rows of one commit take consecutive `seq` values. The commit's last row carries `commit_seq`, the boundary marker.
  Chunks are cut only after a boundary row.
- An offset `O` means: every feed row with `seq ≤ O` was delivered.
- **Write offsets are a different number.** Writes return `commit_offset` today
  ([DocumentRoute.cs#L277](../../src/TerraBase/Features/Documents/DocumentRoute.cs#L277)). It cannot be compared with
  `seq`. v1 makes **no read-your-writes promise**. A client that needs its write waits for the row in the shape.

### 3.3 Handle and shape id

Both are lowercase hex. Fields are UTF-8, joined by one NUL byte, with no trailing NUL. Integers are decimal ASCII.
ULIDs are their 26 canonical characters. The NUL separator is unambiguous because ULIDs are fixed length and canonical
JSON escapes NUL.

```
handle   = "h1-" + hex( first 8 bytes of  SHA-256( "TB-SHAPE-HANDLE-V1" NUL fields ) )     → 16 hex characters
shape_id =        hex( first 16 bytes of  SHA-256( "TB-SHAPE-V1"        NUL fields ) )     → 32 hex characters

fields = organization ‖ project ‖ database ‖ incarnation ‖ replica_id ‖ replica_epoch
         ‖ collection_name ‖ collection_generation ‖ canonical_filter ‖ wire_format
```

- `replica_id` is the serving machine's full `ReplicaId` as decimal uint64. `replica_epoch` is its unclean-start epoch.
  `incarnation` is the store incarnation, drawn on every create, copy, restore and rebuild.
- `collection_name` and `collection_generation` identify the collection and its current create. Generation changes on
  drop or re-create.
- `canonical_filter` is the normalized compact UTF-8 JSON defined in [protocol section 2](shape-reads.md#2-route-and-validation):
  order-independent flattened/sorted/deduplicated `and`/`or` (deterministically regrouped at 64 children), sorted/deduplicated
  `in`/`nin` (singletons become `eq`/`ne`), negation aliases/double-negation removal, whole binary64 values in the int64
  range written as integers, and canonical JSON Pointer escaping. Literal types and parsed values, including exact
  int64 values, are preserved. Both raw and canonical filters remain valid query v1 filters of at most 8 KiB UTF-8,
  within the depth/node limits. Empty when `where` is absent; equivalence is limited to those normalization rules.
- `wire_format` is the literal `1`. It names the wire contract rather than the engine's `StorageFormat.Id`.
- `shape_id` uses the same fields under its own domain, so it names the shape and the lineage together.
- Input fields and the expected `handle` and `shape_id` are in `shape-vectors.json` (`identity_vectors`);
  `filter_vectors` checks raw `where`, its canonical bytes and the derived handle, shape id and resume scope.

**Per machine, not per region.** `replica_id` names one machine. Two machines in one region have different handles and
do not share cache entries. A request whose handle this machine does not know answers `409 must_refetch`; the client
may then try one cursor resume before discarding state, provided its saved cursor is usable (3.7; protocol section 7).

The handle rotates, and clients get `409 must_refetch` or resume with a cursor, when:

- the database is copied, restored or re-created (new incarnation);
- the machine's epoch rises (an unclean start), so cached bytes never cross a serving-lineage change;
- the collection is dropped or re-created (new generation);
- the filter encoding changes;
- the wire format changes.

### 3.4 Sealing and the durable frontier

A background sealer per database runs on the write lane.

1. **Boundary.** A closed chunk covers `(O, O']`. It holds at most 4096 feed rows or 1 MiB of body, cut only after a
   boundary row (3.2). A single commit that does not fit travels alone, up to 8 MiB.
   - **Limitation (explicit).** A commit whose materialized values exceed 8 MiB cannot be served. The sealer answers
     `503 shape_too_large` with `no-store`, and neither the cursor nor `next_offset` advances. Shape reads of that
     database stop at that commit until the sealer can materialize it within the bound. Commits are not split.
2. **Payload.** Each operation carries the document as materialized in one read transaction. That transaction's feed
   head is the object's `values_at`.
   - `values_at` can be newer than `O'`. The value is never older than its event.
   - A delete, or a document that no longer matches the filter, is a `delete` operation.
   - No copy-on-overwrite table is added. The materialization read is the only source of values.
3. **Publication gate.** An object is served only when the durable frontier `F` satisfies:

   ```
   F ≥ max(covered_offset, values_at)
   ```

   - `covered_offset` is the decimal `next_offset` for a chunk or fresh body, and the snapshot's `S` for every page of
     that snapshot.
   - Before FR-25, `F` is the local committed head (FULL). A committed value may appear.
   - After FR-25, `F` is the highest commit-boundary feed `seq` for which every commit at or below it is remote-acked
     under the current incarnation. That is
     the A4 contract, mapped from `CommitMarker` ([CommitResult.cs#L6](../../src/TerraBase/Engine/CommitResult.cs#L6)).
     A sealed object whose `values_at` is ahead of `F` waits, unserved, until the newer values are remote-acked. Since
     the object is fixed once sealed, publication can proceed when the acknowledgements arrive. A partition can leave
     it pending; the sealer never weakens the gate to make progress.
4. **Persistence.** Sealed bodies are written once to `_tb_shape_chunk`, with capture suspended
   ([SuspendCaptureAsync, SqliteStorageTransaction.cs#L1264](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.cs#L1264)).
   They are derived data: not shipped, not backed up, dropped on handle rotation. They are never recomputed. A
   TBS1 object for T1 is produced once, under the epoch current at creation (3.9).
5. **Seal is a committed read.** Every write transaction is already FULL (#181). The seal adds no barrier. FR-07
   checks that Turso's FULL behaves as documented for tenant databases.
6. Writes that are not yet past the gate appear in no response. The open tail (3.6) is served only after a seal and
   the gate.

### 3.5 Snapshots

- A snapshot is the collection's live documents, filtered, at a sealed offset `S`.
- It is paged in id order, at most 1000 documents or 1 MiB per page, with keyset continuation (TS-474's page rules).
- **All pages of one snapshot are materialized in one read transaction at head `S`.** The snapshot is published only
  when `F ≥ S`. Past `S`, nothing is reconstructed from current rows.
- A snapshot is re-sealed when 4096 chunks have accumulated or an hour has passed, whichever comes first.
- `offset=-1` answers `307` to the **newest sealed snapshot**, not to the head. Every new client shares the same
  snapshot URLs, then catches up through chunks.

### 3.6 Live tail and request collapsing

- `live=true` at the head waits up to 20 s for a commit. The response cursor is the live bucket,
  `max(floor(unix_s / 20), input cursor + 1)`. The decimal `cursor` query parameter is the live bucket when `live=true`
  (3.7 covers resume).
- One flight per `(handle, offset, live, cursor)`. At most 32 flights and 256 joined requests. These are TS-474's
  limits and code.
- **Seal-on-wake.** When a flight wakes, it asks the sealer for a seal up to the head before it answers. One seal is
  shared by every waiter of every shape on that database. The tail includes seal latency and, after FR-25, remote-ack
  latency. There is no special FULL barrier.

### 3.7 Region failover without re-snapshot

- Each response body carries `cursor_resume`: an opaque envelope, `base64url(0x01 ‖ resume_scope16 ‖ R8)`. `R8` is the
  `ChangeCursor` the serving machine issued ([ChangeCursor.cs](../../src/TerraBase/Features/Documents/ChangeCursor.cs), #207).
  `resume_scope16` names the collection and filter, not the machine, so it is the same on every machine (`shape-reads.md`
  section 9). Clients store it verbatim.
  - On immutable pages (snapshot pages and chunks) it is computed **once, at seal**, and stored with the object. It
    never changes.
  - On tail and live responses it is fresh for each response.
- **Sending it.** A resume request carries the cursor in exactly one of two places:
  - `?cursor=<opaque>` only when `offset=-1`, no handle is sent, and `live` is absent;
  - the `TB-Shape-Resume: <opaque>` header when the cursor is too large for a URI. It can be about 19 KB base64url,
    and TS-474's request-line limit is 8 KiB. SDKs send the header when the full URI cannot fit.
  - On a resume request, both transports at once or `live=true` is `400`. On other requests the server ignores
    `TB-Shape-Resume`, because HTTP clients retain it when following the resume redirect. Resume redirects are
    `no-store`; bootstrap redirects keep the normal private revalidation policy.
- **Answering it.** The server verifies the proof, then:
  - a malformed envelope or inner cursor is `400`;
  - a scope mismatch (another collection, filter or wire format) is `409 must_refetch`;
  - an expired or behind proof is `409 must_refetch`. Resume fails closed. It never skips on a stamp comparison alone;
  - a valid proof is `307` to the earlier of the commit boundary before the first `seq` the proof does not cover and the
    local sealed head. If the proof covers everything local, the redirect goes to the local sealed head. A target below
    this machine's retention floor is `409 must_refetch`.
- A `410` from the changes cursor is surfaced as `409 must_refetch`. A repair can cause one (replication doc §14).
- **Delivery is at least once.** Clients apply every operation in order (3.10, rule 6), so replays are idempotent.
- A snapshot cursor is safe to resume only after all snapshot pages have been applied. A client that interrupts a
  snapshot restarts at `-1`; it cannot treat a page's cursor as proof that its local replica is complete.
- **Planned, not done.** Resume needs FR-01 (stamp order), #207 (merged), and #211 (persisted proofs across restarts,
  and replay to the issuing machine). Until #211 merges, every resume answers `409 must_refetch`.

### 3.8 Free transport tiers

| Tier | What serves sealed bytes | Origin cost per extra reader | Needs |
| --- | --- | --- | --- |
| **T0** | The origin. Plaintext body, `private, max-age=0, must-revalidate`. Flights plus `ETag`/`304`. The conditional check runs after auth | ~0 storage reads; one HTTP response each | FR-20 |
| **T1** | Tigris. TBS1 ciphertext only, keyed by hashes of `shape_id` and binding, never tenant names. Clients get an authenticated, `no-store` manifest | Origin signs only; Tigris egress and caching apply | FR-21, FR-22 |
| **T2** | CDN in front of the public host | ~0 for hits | **Not selected.** A later human decision |

T1 details:

- **Manifest.** `GET …/shape/manifest?handle=&offset=` returns at most **8** objects, starting with `(handle, offset)`
  and continuing in read order. Each entry has its URL, the ciphertext's SHA-256 and size, its canonical binding, `key_id` and
  epoch. Presigned URLs expire no later than the next epoch boundary (3.9). The manifest is `private, no-store`.
- **Client checks before decrypting.** The client verifies the SHA-256 and size of each fetched object, and checks that
  the header's epoch and `KID` match the manifest, before it fetches a key or decrypts. The client does not trust Tigris
  for integrity.
- **Caching.** T1 objects carry `public, max-age=31536000, immutable` and no `Vary: Authorization`. Presigned URLs
  differ per manifest, so shared-cache hit rates depend on URL keying. FR-22 measures this, along with the Tigris
  pricing check.
- FR-00 is no longer a gate. The remaining caching-proxy question applies only to T2, which is not selected.

### 3.9 Encrypt-then-cache

Exact bytes are in [`shape-reads.md` §12](shape-reads.md#12-tbs1-sealed-bytes). This section states the properties the design depends on.

- **Scope of encryption.** Only T1 objects are encrypted. Origin responses are plaintext to the authorized caller.
- **Format TBS1.** AES-256-GCM, one per-object key, cut into frames.
  - Each object has a random 256-bit salt in its header. Its key is derived from the epoch key and the salt, so no key
    and nonce pair repeats. Nonces are the frame index, so they are unique within an object.
  - Each frame's associated data includes the header, the frame index and the frame's length word. A swapped, dropped or
    reordered frame fails its tag.
  - **Framing rules.** A non-final frame is exactly 1 MiB. The final frame is shorter than 1 MiB. Plaintext that is an exact
    multiple of 1 MiB ends with an empty final frame. The object is at most 8 MiB of plaintext.
  - **Rejected:** frames over 1 MiB, short or empty non-final frames, a missing final frame (truncation), bytes after the
    final frame, reordered frames, tag failure, and more than 8 MiB total.
  - **No compression.** Compression would leak tenant data lengths.
- **Epoch keys.** The epoch key `K_e` is derived from the project data key that `key_id` names, with the handle and
  epoch as its context. Clients receive `K_e`, not the project key.
- **Key endpoint.** `GET …/shape/keys?handle=&epoch=&key_id=`.
  - `key_id` is required. It picks the correct project data key after a rotation.
  - Serves only epochs from the retention floor up to the current epoch. A future epoch is `400 invalid_request`.
    Retired objects or an unknown data-key identity fail closed; exact errors are in the protocol.
  - Scope `documents:read` (3.12). Client listener, same tenancy checks as any data route. `Cache-Control: private,
    no-store`.
  - The retention floor is the oldest epoch whose objects are still served (the feed's 7-day retention). Older keys
    stay available only while their objects are in retention, for authorized resnapshot and catch-up.
- **Epoch length: 10 minutes**, a code constant (D3, settled).
- **Newly created objects use the current epoch key.** Cached objects from older epochs stay as they are. Nothing
  sealed under an old epoch is re-sealed.
- **Revocation bound.**
  - An API key gets no key for any epoch once its revocation reaches the serving machine.
  - TerraBase checks ts-auth JWTs offline. A JWT revoked upstream is accepted until its expiry plus 30 s of clock skew.
    The worst case from upstream revocation is the remaining token lifetime, plus 30 s, plus the remaining epoch (at most
    10 minutes).
  - What a client already read stays read.
- This keeps "always-on capability does not bypass authentication". Authorization happens once per
  `(client, handle, epoch)`, not once per byte.
- **Metadata visible to a cache.** Sizes and object counts are visible in the manifest. FR-34 reviews this.

### 3.10 The contract a shape client gets

1. **Lineage.** A prefix of one machine's apply order, cut on commit boundaries. A commit never splits across objects.
   Values are never older than their event, and may be newer (`values_at`, 3.4).
2. **Freshness.** `up_to_date: true` appears only on tail or live responses whose `next_offset` is the sealed head and
   whose gate has cleared. Immutable pages never say `true`.
3. **Durability.**
   - Today: only what the machine has committed (FULL). **A host loss can erase writes that a chunk has already
     published.** This plan does not claim host-loss durability.
   - After FR-25: only what another region also holds durably.
4. **Atomicity and isolation.** A commit's operations in one collection arrive in the same object. Nothing is promised
   across collections or across regions. **No read-your-writes promise in v1** (3.2).
5. **Engine semantics, inherited.** Last-writer-wins skew, hidden unique losers (`delete` operations), schema checks at
   origin only, and C4 churn.
6. **Apply rule.** On any lineage, including after failover or repair, a client applies every operation in order.
   It does not skip an operation because the same event identity was seen on another lineage. The same event can carry
   newer values there. A `delete` of an unknown id is a no-op.

### 3.11 Bounds and metrics (all code constants)

| Bound | Value |
| --- | --- |
| Chunk | 4096 rows or 1 MiB. A single commit travels alone, up to 8 MiB. Above 8 MiB: `503 shape_too_large` |
| Snapshot page | 1000 documents or 1 MiB |
| Flights | 32 running, 256 joined |
| Filtered shapes | 64 per database; eviction answers `409` |
| Manifest | 8 objects per response |
| Epoch | 10 minutes |
| Memory | Every seal, page or flight reserves through `IMemoryBudget` |
| Retention | Chunks and snapshots follow the 7-day feed retention; below the floor answers `409 must_refetch` |

Metrics on `TerraScale.TerraBase`, without tenant labels:

- `terrabase.shape.reads`
- `terrabase.shape.requests.waiting`
- `terrabase.shape.flights.active`
- `terrabase.shape.seals` (count and seconds)
- `terrabase.shape.sealed_bytes`
- `terrabase.shape.mirror.lag`
- `terrabase.shape.redirects`

### 3.12 Rule compliance

- **Always on.** No flag, setting or toggle. All values are constants.
- **APIs.** `/v1`, built on TerraScale.Endpoints, OpenAPI through Scalar, source-generated serializer context.
- **Scope.** `documents:read` for the shape route and the key and manifest routes. ts-auth already defines `documents:read` ([ApiKeys.cs#L22](../../src/TerraScale.Authentication/Features/Platform/ApiKeys.cs#L22)). No new scope is needed.
- **Data placement.** T0: sealed bodies in TerraBase's database file, as uncaptured derived data. T1: TBS1 ciphertext in
  Tigris. No new store.
- **Tests.** Integration tests only, through `TerraBaseTestServer`. Every test runs in parallel. Fast covers the
  critical path.
- **Ownership.** TerraBase owns reads. A CDN (T2) would be an edge component outside the OCI origin, so it needs a human decision.

---

## 4. Durability baseline

### Turso dependency, exact

| Part | Version and pin | File |
| --- | --- | --- |
| Native core (`libturso_sdk_kit`, prebuilt) | `Turso.Data.Native` **0.8.2** | [Directory.Packages.props#L69-L70](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/Directory.Packages.props#L69-L70) |
| Managed driver (vendored C#) | upstream tag `v0.8.2`, commit `5c168be5ef6a32ac966daf5983502a4f694b37ad` | [src/vendor/Turso.Data.Sqlite/UPSTREAM.md#L11-L12](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/vendor/Turso.Data.Sqlite/UPSTREAM.md#L11-L12) |
| Reference from TerraBase | `ProjectReference` to the vendored `Turso.Data.Sqlite.csproj` | [src/TerraBase/TerraBase.csproj#L69-L70](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/TerraBase/TerraBase.csproj#L69-L70) |
| Driver regression tests | `tests/Turso.Data.Sqlite.Tests`, run first by `make terrabase-test-fast` | `Makefile` |

### Tenant synchronous setting

- Tenant connections set `PRAGMA synchronous = NORMAL`
  ([SqliteSession.cs#L87-L96](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/TerraBase/Engine/Sqlite/SqliteSession.cs#L87-L96)).
- Since #181, `BeginAsync` sets `durable |= write`. Every write transaction syncs at commit
  ([SqliteDatabaseStore.cs#L226](../../src/TerraBase/Engine/Sqlite/SqliteDatabaseStore.cs#L226)). Group commit still passes
  `durable: false` ([SqliteDatabaseStore.cs#L687](../../src/TerraBase/Engine/Sqlite/SqliteDatabaseStore.cs#L687)), but
  that argument no longer decides writes. Read-only transactions are unaffected.
- Decision 0003 states SQLite's WAL semantics: process crash loses nothing; OS crash or power loss loses commits since the
  last WAL sync; torn frames are discarded
  ([0003#L22-L33](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/docs/terrabase/decisions/0003-sqlite-synchronous-normal.md#L22-L33)).
- Evidence for **Turso 0.8.2** is limited to:
  - a spike strace showing no fsync per commit under the earlier setting
    ([turso-spike.md#L187](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/docs/terrabase/turso-spike.md#L187));
  - an E2E fault-shim power-loss test for the **system** database under FULL, which CI does not run.
- No test establishes Turso's FULL behaviour for **tenant** databases: that a commit's sync covers earlier frames,
  torn-frame recovery, or the effect of checkpoints. FR-07 is still open. Until it runs, FULL is unproven for tenants.

### Host-loss exposure, independent of fsync

- A write answers success after its local commit. There is **no remote ack wait and no backup wait**. The route
  reports `Durability = "local_durable"`. Decision 0013a is still *proposed* (revision 4).
- Losing the host or volume before the write ships to a peer or to Tigris loses an acknowledged write. This holds
  whatever local fsync does.
- Replicated applies commit with FULL
  ([StorageChangeGroupSink.cs#L37](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/src/TerraBase/Features/Replication/StorageChangeGroupSink.cs#L37)).

### Consequence for free reads

A published chunk is permanent and global. If it contains a write that later disappears, client replicas diverge from
TerraBase replicas, and no repair reaches them. So:

- seal committed rows now, with no extra barrier (3.4, step 5);
- gate publication on the durable frontier (3.4, step 3); the frontier moves to the remote-ack frontier with FR-25;
- until FR-25, state the host-loss exposure in the contract (3.10, rule 3). Do not claim host-loss durability;
- never publish the unsealed tail.

---

## 5. Model routing

| Rule | Model |
| --- | --- |
| The item's required tests run in **10 minutes or less** | **Claude Haiku 5.5**, `claude-haiku-5-5` |
| The item's required tests take **more than 10 minutes**, or their runtime is unmeasured | **Claude Opus 5.5**, `claude-opus-5-5` |
| Haiku 5.5 unavailable | `gpt-6.1-sol` through Codex, after confirming it in the live catalog |
| Never | Claude Haiku 4.5, under any id, or any silent fallback. If neither model is available, stop and report it |

- Haiku items are sized to about 10 minutes of work. A Haiku item that overruns is handed to Opus. It is not split
  mid-flight.

Test-time classes. Measured values are cited; everything else is an estimate.

| Class | Command (prefix locally with `nice -n 19`) | Time | Model |
| --- | --- | --- | --- |
| Fast, filtered | `make terrabase-test-fast TEST_ARGS="--treenode-filter '/*/*/<Class>/*'"` | ~2–4 min. Measured: 67 tests in 28 s, after a 1 min 43 s build | Haiku |
| Fast, full | `make terrabase-test-fast` | ~9 min (CI `terrabase` job 9 min 8 s, run 38393279132) | Haiku |
| Docs only | none | 0 | Haiku |
| Slow | `make terrabase-test-slow` | unmeasured (the only CI sample, 3 min 48 s, failed) | Opus until measured ≤10 min |
| E2E (in-process, process-separated, fault shim) | `make terrabase-e2e TEST_ARGS=…` | up to 30 min (CI timeout) | Opus |
| Benchmarks, live OCI, real Tigris | `benchmark/…`, runbooks | >10 min | Opus |

---

## 6. Work plan: parallel waves

- One PR per item, on branch `feat/free-reads-<id>`.
- Items in the same wave do not share files, except where *Depends* says so.
- New code goes in `src/TerraBase/Features/Shapes/`, plus partial files `Engine/Sqlite/SqliteStorageTransaction.Shape*.cs`.
- New integration tests go in `tests/TerraBase.Fast/Shapes/<Class>Tests.cs` for critical paths, or
  `tests/TerraBase.Slow/Shapes/<Class>Tests.cs` for fault, stress and broader end-to-end cases. They run in parallel;
  no new test suite is introduced.

### Wave 0: decisions and checks

| ID | Work | Who | Done when |
| --- | --- | --- | --- |
| FR-00 | Caching-proxy and pricing answers for T2 only. Not a gate for T0 or T1 | human (production and account access) | Answers recorded before any T2 decision |

### Wave 1: foundations (no dependencies)

| ID | Work | Files owned | Depends | Tests | Time | Model |
| --- | --- | --- | --- | --- | --- | --- |
| FR-01 | **Done (#222).** Per-origin stamp order (C7, lane A2): a mint never goes below this node's last mint; `Observe` stays capped; seed from the largest stored stamp at open | `Engine/MemberClock.cs`, `Engine/Sqlite/SqliteStorageTransaction.States.cs` | — | Fast filtered: `Replication/OriginStampOrderTests` | ~3 min | Haiku |
| FR-02 | Repairs in the feed (C3) | — | — | — | — | **Done** (#165) |
| FR-03 | **Done (#221).** Storage (lane B1): `_tb_feed.seq` from a persisted counter row; `_tb_shape_chunk`, `_tb_shape_snapshot`; storage format version bump (greenfield reset) | `Engine/Sqlite/SqliteDatabaseStore.cs` (schema), `Engine/Sqlite/StorageFormat.cs`, `SqliteStorageTransaction.Feed.cs` (`AppendFeed` only) | — (coordinate with #211) | Fast filtered: `Shapes/FeedSequenceTests` (an emptied feed plus a reopen keeps growing) | ~3 min | Haiku |
| FR-04 | Protocol and vectors (lane B0): `shape-reads.md` as v1, and `shape-vectors.json` | `docs/terrabase/shape-reads.md`, `docs/terrabase/shape-vectors.json` | — | none (docs) | 0 | Haiku |
| FR-05 | Peer authentication (C1, lane A1): a shared credential delivered from OCI Vault via instance principal on every internal RPC; anything without it is refused | `Features/Replication/*`, `Hosting/Listeners.cs` | — | Fast with 2 nodes, plus process-separated E2E | >10 min | Opus |
| FR-06 | **Done (#223).** Fan-out benchmark harness (lane B2): N readers per shape; origin reads, bytes, p99; results as `.md` in `benchmark/bench/` | `benchmark/cmd/shape-bench/` | — | benchmark run | >10 min | Opus |
| FR-07 | Turso FULL crash and power-loss evidence for tenant databases (lane B3): an acknowledged write survives power loss, and a commit's sync covers earlier frames | `tests/TerraBase.Slow/WalFaults/TenantDatabasePowerLossTests.cs` | — | E2E (fault shim) | >10 min | Opus |

### Wave 2: core (each needs FR-03; all parallel)

| ID | Work | Files owned | Depends | Tests | Time | Model |
| --- | --- | --- | --- | --- | --- | --- |
| FR-10 | Slice and endpoint skeleton: route, parameter validation, `307` from `-1`, `409 must_refetch`, headers, OpenAPI/Scalar, serializer context, `documents:read`, client listener | `Features/Shapes/{GetShapeEndpoint,ShapeModels,ShapesExtensions}.cs`, one line in `Program.cs` | FR-03 | Fast filtered: `ShapeApiTests` | ~4 min | Haiku |
| FR-11 | Handle and shape id (3.3), canonical filter, rotation triggers | `Features/Shapes/ShapeHandle.cs` | FR-03 | Fast filtered: `ShapeHandleTests` (rotates on drop and re-create, copy, restore, unclean start) | ~4 min | Haiku |
| FR-12 | Chunk sealer (3.4): commit-boundary chunks, `values_at`, publication gate, committed seal (no extra FULL barrier), capture suspended, memory reserved, damaged rows refused as in #152, `503 shape_too_large` | `Features/Shapes/ShapeSealer.cs`, `Engine/Sqlite/SqliteStorageTransaction.Shapes.cs` | FR-03 | Fast filtered: `ShapeSealTests` (1 node; 2 nodes with joined writes) | ~5 min | Haiku |
| FR-13 | Snapshot sealer (3.5): one read transaction at `S`, gate `F ≥ S` | `Features/Shapes/ShapeSnapshots.cs`, `Engine/Sqlite/SqliteStorageTransaction.ShapeSnapshots.cs` | FR-03 | Fast filtered: `ShapeSnapshotTests` | ~5 min | Haiku |
| FR-14 | Live tail (3.6): 20 s long-poll, cursor buckets, flights, seal-on-wake | `Features/Shapes/ShapeFlights.cs` | FR-03 | Fast filtered: `ShapeLiveTests` | ~5 min | Haiku |
| FR-15 | Filtered shapes: canonical `where` (the query v1 subset), evaluated at seal, 64 per database, eviction answers `409` | `Features/Shapes/ShapeFilters.cs` | FR-03 | Fast filtered: `ShapeFilterTests` | ~5 min | Haiku |
| FR-16 | Retention and floors: follow the feed's 7 days; below the floor answers `409` | `Features/Shapes/ShapeRetention.cs` | FR-03 | Fast filtered: `ShapeRetentionTests` | ~4 min | Haiku |
| FR-17 | Metrics and memory admission (3.11) | `Features/Shapes/ShapeMetrics.cs` | FR-03 | Fast filtered: `ShapeMetricsTests` | ~3 min | Haiku |

### Wave 3: wire-up, encryption, transport, failover

| ID | Work | Files owned | Depends | Tests | Time | Model |
| --- | --- | --- | --- | --- | --- | --- |
| FR-20 | Wire FR-11 to FR-17 into the endpoint; T0 headers (`private`, `ETag`, `304`); `TB-Shape-Resume` parsing | `Features/Shapes/GetShapeEndpoint.cs` | FR-10 to FR-17 | Fast, full | ~9 min | Haiku |
| FR-21 | Encrypt-then-cache (3.9): TBS1, epoch keys, `shape/keys` (`key_id` required), `shape/manifest`, epoch constant | `Features/Shapes/{ShapeCrypto,GetShapeKeyEndpoint,GetShapeManifestEndpoint}.cs` | FR-10 | Fast filtered: `ShapeCryptoTests` (wrong epoch, handle or key id fails; revoked key gets nothing new; future epoch refused) | ~5 min | Haiku |
| FR-22 | T1 Tigris mirror: PUT TBS1 objects once; manifest with URL, SHA-256, size and binding; Tigris pricing check | `Features/Shapes/ShapeMirror.cs` (reuses `Features/Backup/ObjectStore.cs` read-only) | FR-12, FR-13, FR-21 | Fast filtered with `FakeS3`: `ShapeMirrorTests` | ~5 min | Haiku |
| FR-23 | Region failover (3.7): verified `cursor` or `TB-Shape-Resume`, then `307` to a commit boundary; at least once | `Features/Shapes/ShapeResume.cs` | FR-01, FR-20, #207 (merged), #211 | 3-region E2E | >10 min | Opus |
| FR-24 | Shared-cache headers on T1 objects: `public, max-age=31536000, immutable`; errors stay `no-store` | `Features/Shapes/ShapeMirror.cs` (headers only) | FR-05, FR-21, FR-22 | Fast filtered: `ShapeHeaderTests` | ~3 min | Haiku |
| FR-25 | Durable frontier (lane A3): publish only when `F ≥ max(covered_offset, values_at)` with `F` the remote-ack frontier; unacked-overwrite test | `Features/Shapes/ShapeSealer.cs` | 0013a accepted; write wait (A3) merged; #191 (merged); FR-12 | E2E across 2 regions, with host loss; unacked-overwrite test | >10 min | Opus |

### Wave 4: verification

| ID | Work | Depends | Tests | Time | Model |
| --- | --- | --- | --- | --- | --- |
| FR-30 | A crash or power loss never leaves a published chunk holding a lost write (lane V1) | FR-07, FR-12 | E2E | >10 min | Opus |
| FR-31 | 3-region shape clients converge with the replicas under partition, drop/re-create, repair and skew (lane V3) | FR-20, FR-23 | E2E | >10 min | Opus |
| FR-32 | Load: 10k readers per shape; at most 1 origin storage read per chunk per cache location; report in `benchmark/bench/` | FR-06, FR-24 | benchmark | >10 min | Opus |
| FR-33 | Real Tigris mirror run (credentials delivered from OCI Vault via instance principal, run by a human) | FR-22 | Slow `TigrisShapeMirrorTests` | unmeasured | Opus |
| FR-34 | Security review: cache keys, tenancy, revocation bound, ciphertext metadata leakage (lane V4) | FR-21, FR-24 | E2E plus review | >10 min | Opus |

### Wave 5: SDKs (separate repositories; all parallel after FR-04 and FR-21)

| ID | Work | Repository | Lane | Tests | Model |
| --- | --- | --- | --- | --- | --- |
| FR-40 | Shape client: snapshot, chunks, live, `must_refetch`, cursor resume, WebCrypto decrypt, conformance vectors | ts-js | S1 | its suite, ≤10 min | Haiku |
| FR-41 | Same | ts-go | S2 | ≤10 min | Haiku |
| FR-42 | Same | ts-dotnet | S3 | ≤10 min | Haiku |
| FR-43 | `shape tail` command | ts-cli | none in the replication plan | ≤10 min | Haiku |
| FR-44 | Same as FR-40 | ts-rust | S4 | ≤10 min | Haiku |

If an SDK's suite runs longer than 10 minutes, that item moves to Opus.

### Wave 6: Tier B, full free replication (optional; D7)

| ID | Work | Tests | Model |
| --- | --- | --- | --- |
| FR-50 | Per-origin delta chunks in Tigris: canonical change groups, shared across regions | E2E | Opus |
| FR-51 | Bootstrap a new machine or region from a Tigris snapshot plus delta chunks, replacing anti-entropy refill | E2E | Opus |
| FR-52 | Lattice join and materialization in the SDKs, with conformance vectors from `DocumentState` | SDK suites, multi-language | Opus |

**Critical path:** FR-03 → FR-12 → FR-20 → FR-24, which needs FR-05 and FR-21 → FR-32.

**Totals:** 30 open items (FR-01, FR-02, FR-03 and FR-06 are done): 18 for Haiku and 12 for Opus.

**Lane mapping** (`.cos/replication-improvement-plan.md`, section 2): FR-01 = A2; FR-05 = A1; FR-25 depends on A3
(and A4/A5 where they touch ack code); FR-03 = B1; FR-06 = B2; FR-07 = B3; FR-04 = B0; FR-30..34 = V1..V4 (V5 is live
qualification of wave A). FR-40..42 and FR-44 = S1..S3 and S4; FR-43 (ts-cli) has no lane in that plan.

---

## 7. Decisions (settled 2026-10-13)

| ID | Decision | Status |
| --- | --- | --- |
| D1 | Transport T0 (origin) and T1 (Tigris). No CDN (T2) selected | Settled |
| D2 | Encrypt-then-cache with a presigned manifest. No unencrypted T1 path | Settled |
| D3 | Epoch length 10 minutes (code constant). Live bucket 20 s | Settled |
| D4 | Seal committed rows now (FULL since #181, no extra barrier). Publication gated on the durable frontier; the frontier moves to remote ack with FR-25 | Settled |
| D5 | Sealed bodies: uncaptured derived data in the DB file for T0; TBS1 ciphertext in Tigris for T1; 7-day retention | Settled |
| D6 | This file lives at `docs/terrabase/free-reads.md` | Settled |
| D7 | Tier B (wave 6) | Deferred until V3 numbers exist |
| D8 | Shape offset is `_tb_feed.seq` from a persisted counter. Write `commit_offset` is not a shape offset. No read-your-writes promise in v1 | Settled |
| D9 | Handle is `h1-` plus 8 bytes of SHA-256 (16 hex); shape id is 16 bytes (32 hex). Both bind the serving machine (per machine, not per region) | Settled |
| D10 | Resume cursor is opaque. Sent as `cursor` query or `TB-Shape-Resume` header, never both. Live bucket keeps the `cursor` name when `live=true` | Settled |
| D11 | No commit splitting. A commit over 8 MiB answers `503 shape_too_large` and does not advance | Settled, explicit limitation |
| D12 | Values are read in the materialization transaction; `values_at` gates publication. No copy-on-overwrite table | Settled |
| D13 | Scope `documents:read` for the shape, key and manifest routes | Settled |
| D14 | Resume token is `base64url(0x01 ‖ resume_scope16 ‖ R8)`. `resume_scope16` binds organization, project, database, collection, collection generation, canonical filter and wire format, not the machine. Malformed is `400`, mismatched scope is `409` (B0 review) | Settled |
| D15 | Publication gate uses `covered_offset`: the `next_offset` for chunks and fresh bodies, the snapshot's `S` for every page (B0 review) | Settled |
| D16 | Region-failover redirect goes to the earlier of the commit boundary before the first uncovered row and the local sealed head; a target below retention is `409` (B0 review) | Settled |
| D17 | Object keys in Tigris are `v1/shapes/<SHA-256(shape_id ‖ 0x00 ‖ binding)>` (B0 review) | Settled |

---

## 8. Definition of done (Tier A)

1. Load test (FR-32): 1,000 live readers of one shape on one machine cost at most 1 storage read per commit on that
   machine. With T1, origin egress for sealed bytes is about 0 (redirects and manifests only).
2. Crash test (FR-30): no published byte contains a write that a crash later removes.
3. Failover test (FR-31): a client that changes region resumes without a re-snapshot and converges.
4. Revocation: once an API key's revocation reaches the serving machine, it gets no key for any epoch. A revoked JWT is
   bounded by expiry, 30 s of skew and the remaining epoch (3.9).
5. API and tests: `/v1`, OpenAPI/Scalar, documented in `api.md` and `shape-reads.md`. Fast covers FR-10 to FR-24;
   E2E covers FR-23, FR-25, FR-30 and FR-31.

---

## 9. Risks

| Risk | Mitigation |
| --- | --- |
| Write amplification: sealed bodies copy documents | Measure in FR-32; on T1, keep sealed bytes in Tigris only |
| Seal fsync cost on the single write lane | One fsync per seal, shared by every waiter; size-bounded batches |
| Storage grows with the number of regions (each machine seals its own chunks) | Tier B dedupes across regions |
| Values newer than a chunk's `next_offset` | Publication gate (3.4, step 3) and `values_at` |
| A commit over 8 MiB stalls a shape | Explicit limitation (3.4, step 1); splitting is a later decision |
| Host loss before FR-25 erases a published write | Stated in the contract (3.10, rule 3); not claimed as durable |
| Turso FULL not proven for tenant DBs; a publish could contain a write a power loss removes | FR-07 before the first public chunk; FR-30 after |
| Remote-ack publication frontier not wired (0013a waits are implemented for document and transaction routes) | FR-25 must still move publication from the local committed head to a proven remote frontier |
| Unacked overwrite leaks into a published object after FR-25 | `values_at` gate, and its unacked-overwrite test (FR-25) |
| Resume trusts a stamp without a proof, or persisted proofs are lost on restart | Verify #207 proofs; gate on #211; until then every resume is `409` |
| Repair-induced `410` on cross-region resume | Surfaced as `409 must_refetch` (3.7) |
| Shared-cache hit rate depends on presigned URL keying | FR-22 measures it |
| C4 drop churn, C5 log pinning, H1 and H2 stream timeouts (audit) | Tracked in the audit's ranked fix list; outside this plan |
| Not wire-compatible with Electric's own clients | TerraBase SDKs (wave 5) with conformance vectors |

---

## 10. References

- Replication audit, 2026-10-12/13: findings C1–C10 and H1–H6, base `e30ffdc`, re-checked at `f7d5c3c`.
- Changes merged since `f7d5c3c`, on `main` at `519c9ff9`: #165, #181, #184, #191, #207. #211 open.
- Opaque changes cursor: [`ChangeCursor.cs`](../../src/TerraBase/Features/Documents/ChangeCursor.cs) (#207).
- Wire protocol: [`shape-reads.md`](shape-reads.md). Conformance vectors: [`shape-vectors.json`](shape-vectors.json).
- TS-474 shape reads: `TS474/docs/terrabase/shape-reads.md` (permalink in 2.4).
- R8 feed: [multi-region-replication.md §14](https://github.com/TerraScale/terrascale/blob/f7d5c3c45a45c58495068f1874e4a0c7e5465608/docs/terrabase/multi-region-replication.md#L482).
- Decision 0013a: [decisions/0013a-remote-durable-acknowledgement.md](decisions/0013a-remote-durable-acknowledgement.md).

---

## 11. Conformance vectors

The vector format and its groups are normative in [`shape-reads.md` section 16](shape-reads.md#16-conformance-vectors).
This plan does not restate them.
