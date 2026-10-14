# Shape reads (protocol v1)

Shape reads synchronize one project-scoped collection, optionally filtered by a
query predicate. The Rust SDK implements the protocol from
[TerraScale/terrascale commit 993927ce7ecf8f9b889d24d4d1d21ec96eaf9416](https://github.com/TerraScale/terrascale/blob/993927ce7ecf8f9b889d24d4d1d21ec96eaf9416/docs/terrabase/shape-reads.md).
The server's implementation and deployment status is tracked by
[free-reads.md at that commit](https://github.com/TerraScale/terrascale/blob/993927ce7ecf8f9b889d24d4d1d21ec96eaf9416/docs/terrabase/free-reads.md).
SDK conformance does not claim that every server lane has shipped.

```rust,no_run
use futures_util::{pin_mut, StreamExt};
use terrascale::v1::{Client, Filter};

# async fn example(project: &str, access_token: &str) -> terrascale::v1::Result<()> {
let client = Client::for_project(project, access_token).build()?;
let notes = client.database("demo").collection("notes");
let updates = notes.shape().filter(Filter::eq("/read", false))?.stream();
pin_mut!(updates);
while let Some(update) = updates.next().await {
    let update = update?;
    println!("{} documents, current: {}", update.state.len(), update.up_to_date);
    // update.state.get(id) returns its version and exact JSON document bytes.
}
# Ok(())
# }
```

`shape()` performs no I/O. `.filter_json(raw_where)?` accepts raw JSON filter
text when needed; the server validates and canonicalizes the predicate. Omit
the filter to synchronize every document. Each `ShapeUpdate` retains an
immutable state snapshot, so later responses do not change earlier updates.
An encoded request target exceeding the protocol limit fails locally before
the request is sent, even when the raw filter fits its separate 8 KiB bound.
The stream ends after a fatal error or exhausted retries; create a new stream
to restart with empty state. No state or credentials are persisted to disk.

The stream starts at `offset=-1`, follows the same-origin `307` to a snapshot,
pages that snapshot completely, follows sealed chunks by offset, and long-polls
with `live=true` and the returned `TB-Shape-Cursor` bucket. Upserts replace a
local document and its version; deletes remove it, including harmless deletes
for unknown ids. Each whole response is validated before its state and
checkpoint become visible. Only fresh responses can report `up_to_date=true`.
Non-live freshness requires an empty response at the requested offset. The
existing HTTP response bound leaves room for the envelope around a legal
8 MiB materialized commit or singleton snapshot document.
That describes the serving lineage, not global convergence or read-your-writes.

Every repeated operation is applied. Protocol v1 section 7 forbids deduplicating
by `(origin, stamp, collection, id)`: a repeated event can carry a newer
materialized document. A usable opaque `cursor_resume` is retained only after
all snapshot pages have been applied. The token is passed byte for byte;
large resume tokens use `TB-Shape-Resume` instead of the query parameter.

A changed endpoint or a changed-handle `409 must_refetch` permits one resume
attempt against the saved complete state. An unsuccessful resume, a second
`409` during its redirect follow-up, or an unusable snapshot cursor discards the
state and restarts at `-1`. Other `409 must_refetch` responses also restart.
Bootstrap restarts are bounded, and a proven resume must redirect to a decimal
commit boundary rather than a snapshot page.
`503 shape_too_large`, `503 unavailable`, and `429 shape_busy` preserve state and
offset while the existing client retry policy applies. Caller cancellation
stops the pending request; no background subscription is spawned.

TBS1 uses the existing `ring` dependency for HKDF-SHA256 and AES-256-GCM. An
object is authenticated in full before any plaintext is returned. `Shape::decrypt_sealed` fetches missing epoch keys from the authorized
`shape/keys` route with `Cache-Control: no-store`. Keys are cached only in process
memory by `(handle, epoch, key_id)`, shared by clones of the same client.
Before each shape read or decryption, past epochs are evicted; historical
objects fetch an authorized key without retaining it. Any shape or key-endpoint
`409` clears the client's key cache. Different credentials never share a cache,
and no key is persisted or reused across handle, epoch or data-key rotations.
Key material must never be logged or persisted. An old object retains its
original epoch and data-key id; new epochs fetch their own keys.
The existing `zeroize` dependency clears owned key buffers on drop, and
successful key-response bodies are excluded from error diagnostics.

Object-storage `302` redirects and presigned manifests are deferred until the
FR-22 contract is finalized. The stream reads JSON from the authorized origin;
it never follows foreign redirects or forwards its bearer credential to an
object-store host.

The unchanged upstream vectors and their source commit are in
[`tests/fixtures`](../tests/fixtures/shape-vectors.source.md). Local and CI
qualification uses HTTP fakes, apply vectors, and positive, rejection, and
generated TBS1 vectors. No TerraBase binary is downloaded or run here. Live
server qualification belongs to the TerraScale monorepo.
