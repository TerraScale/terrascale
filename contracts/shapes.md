# TerraBase shape reads: Go SDK contract

The SDK consumes shape reads protocol v1 through `v1.Collection.Subscribe`.
The pinned producer contract describes a proposed protocol; local fixtures do
not establish that the endpoints are deployed or available on TerraBase main.

## Pinned sources

Source commit: `993927ce7ecf8f9b889d24d4d1d21ec96eaf9416` in TerraScale/terrascale.

- [shape-reads.md](https://github.com/TerraScale/terrascale/blob/993927ce7ecf8f9b889d24d4d1d21ec96eaf9416/docs/terrabase/shape-reads.md): normative v1 protocol.
- [free-reads.md](https://github.com/TerraScale/terrascale/blob/993927ce7ecf8f9b889d24d4d1d21ec96eaf9416/docs/terrabase/free-reads.md): producer implementation plan and limits.
- [shape-vectors.json](https://github.com/TerraScale/terrascale/blob/993927ce7ecf8f9b889d24d4d1d21ec96eaf9416/docs/terrabase/shape-vectors.json): copied unchanged into [testdata](../v1/testdata/shape-vectors.json); [provenance](../v1/testdata/README.md).

Precedence for this lane: specification, then vectors, then the work brief.
Repeated events are applied under section 7 rule 4. Object-storage transport
(302 and manifests, including `presigned-manifest`) is deferred until FR-22.
The origin's normal 307 bootstrap and resume redirects remain in scope.

## SDK behavior

| Boundary | Behavior |
| --- | --- |
| Subscription | `Collection.Subscribe(ctx, ShapeOptions{Where: filter})` returns `iter.Seq2[ShapeState, error]`, matching the existing change-feed style. Each update contains current documents, handle, shape id, next offset, usable resume cursor and freshness. Breaking iteration stops requests; context cancellation stops requests and retry waits. |
| Authentication | Existing project `Token` or `TokenSource`; `documents:read` is the producer scope. Refresh an invalidatable source once after 401. Preserve ordinary `APIError` status, code, body and request metadata. No permission widening. |
| Bootstrap | Start empty at `offset=-1`, follow a single 307 only within the same authorized origin and collection, retaining the server's canonical filter. Page the snapshot before reading decimal offsets. |
| Apply | Validate the entire response before changing state. Apply each commit in order, replacing upserts and removing deletes (unknown ids are harmless). Preserve exact document JSON bytes and opaque versions. Yield independently owned state and checkpoint together. |
| Freshness | Read sealed chunks until a fresh response says `up_to_date:true`, then long-poll with `live=true`. Pass each response's `TB-Shape-Cursor` back as the next live bucket. Snapshots and sealed chunks never establish freshness. |
| Machine change | OCI routing uses the same global origin. A changed-handle continuation 409 may trigger one opaque cursor resume at `offset=-1`; an incomplete snapshot has no usable cursor. Any must-refetch on that attempt drops state and restarts without a cursor. No regional discovery or endpoint failover is added. |
| Capacity | `429 shape_busy`, `503 shape_too_large` and `503 unavailable` keep the checkpoint and retry with the existing bounded-delay backoff until cancelled. Other errors follow existing read retry/error rules. |
| Conditional cache | The subscription does not send conditional requests or maintain an HTTP response cache. An unsolicited 304 is an error; `conditional-304` tests a producer/cache behavior outside this iterator. |
| Keys | `Collection.ShapeKey(ctx, handle, epoch, keyID)` shares authenticated no-store fetches and caches keys in process memory by `(handle, epoch, keyID)` within the client's collection scope. Past epochs expire at their 10-minute boundary; shape/key 409 responses evict that collection's cache and invalidate pending loads. Historical keys can be fetched but are not retained past their epoch. Keys are never persisted or prefetched. Returned `ShapeKey` values have caller-owned metadata and can decrypt their own handle/epoch/key id. |
| TBS1 | `ShapeKey.Decrypt(offset, ciphertext)` derives the object key from the caller's handle and offset, checks expected epoch/key id, and decrypts AES-256-GCM frames with the v1 nonce and associated data. Every framing, size, trailing-byte or authentication failure rejects the whole object. |

`ShapeState.CursorResume` stays empty until the last snapshot page has been
applied. Consumers persisting state must persist its entire checkpoint with
the documents. A reset update has empty documents and offset `-1`; a terminal
error carries the last valid state.

Each update copies the current document map and JSON bytes once for caller
ownership. Per-page snapshot updates therefore cost O(pages × documents) in
total copying; retain only needed states for large collections. Internal apply
does not copy the map: complete body and header validation precedes mutation.
Protocol member names are exact; case aliases and duplicate protocol members
are rejected while additional unknown members remain compatible. Repeated
bootstrap refusals use a cancellable backoff after the first immediate restart.
Epoch-key diagnostics redact secret bytes.
The key bytes are held behind a function so reflection cannot print them.
Each client represents one principal; token refresh must retain that principal.
Live waiters retry a shared key fetch if its initiating caller cancels.

The shape body carries materialized values that may be newer than their event.
The vector `cross-lineage-repeat-has-newer-materialized-value` requires applying
the repeated operation. Event-identity or stamp deduplication would lose data.
Versions returned by shape upserts can be passed to existing conditional writes.

## Conformance and qualification

Tests use only synthetic `httptest` exchanges and the pinned vector file.
They never download, build or run TerraBase. Client checks cover offset grammar,
atomic apply, snapshot/chunk/live sequencing, cursor resume/reset, cancellation,
credential forwarding/refusal, epoch key responses, and positive, negative and
generated TBS1 vectors. Identity generation, filter canonicalization and durable
publication remain producer responsibilities; the SDK passes query-v1 filters
and opaque cursors without reconstructing them.

The `presigned-manifest` vector remains in the source file unchanged and is not
claimed as client conformance. Direct object fetching, presigned size/hash/chain
verification and manifest fallback remain deferred. TBS1 decryption and the key
endpoint are available separately; the origin subscription consumes plaintext
JSON, as specified by section 11. No real object store is used for qualification.

Producer limitations remain: no read-your-writes mapping, possible destination
value regression on resume, and host-loss durability gaps before FR-25. Resume
proof support is gated by producer #211/FR-23; clients handle both proven resumes
and refusal. No local fixture proves live deployment, durability, retention or
cross-region completeness.
