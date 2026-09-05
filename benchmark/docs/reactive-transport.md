# Reactive HTTP and WebSocket delivery

Attach with `POST /api/v1/reactive/views` using the database credential. The
response names the canonical view, generation, snapshot URLs, log URL, and view
capability. Client subscription names remain local: incoming frames identify the
canonical view returned by attach. A client verifies that identity before
applying a frame to its local subscription.

HTTP delivery uses the capability header named by the attach response. The
optional browser WebSocket endpoint is
`/api/v1/reactive/views/{view}/{generation}/websocket`. Offer these subprotocols:

- `terrascale.sync.v1`
- `terrascale.cap.<capability from attach>`

The capability travels in the `Sec-WebSocket-Protocol` request header. The
server selects only `terrascale.sync.v1`; credentials never enter the URL or
selected protocol. Each connection is bound to its canonical view and generation.
A Subscribe frame must name that view and repeat the attached query and parameter
bytes. A capability cannot authorize another query. Expiry is checked during
streaming, including idle periods.

Send the latest installed checkpoint to resume. Without a checkpoint, Subscribe
returns the retained snapshot. Subsequent snapshot, delta, and reset frames use
the same immutable bytes as HTTP. Unsubscribe stops the named subscription.
Expired retained history returns a reset and current snapshot. Sends have a
five-second deadline; disconnected sockets release their pending shared polls.

Cross-origin HTTP uses explicit bearer/capability headers, without cookies.
Members answer browser preflights before authentication and expose ETags,
session, placement, Region, and retry headers. The Snippet answers view-read
preflights locally with `no-store`; the subsequent read still requires a valid
capability. Both use `Access-Control-Allow-Origin: *` without credential mode
or origin-dependent response bytes.

SQL views on one member host and tenant share a publication watch and a bounded
journal replay buffer. The buffer retains up to 4,096 records and 8 MiB of
accounted record data, including causal context and retained before-images. Concurrent views reuse the
same immutable record allocations and select only records within their own
snapshot cuts. Byte eviction, excessive backlog, or expired source history
requires a view to rebuild at its exact immutable snapshot; none can become a
silently skipped delta prefix. The source watch detaches when its last owner
is dropped.

Each view registers a conservative interval from its compiled predicate.
The source matches both old and new row values, so updates that leave a
predicate and deletes still reach the old view. Match decisions are shared in
a bounded memo (4,096 records / 8 MiB of accounted data). Unknown predecessors
after pruning include every view of the collection. Numeric bounds deliberately
overmatch rounded values; the native evaluator retains exact integer semantics.
Predicates without a usable scalar interval use collection-wide matching.
Irrelevant rows advance only the source watermark, before candidate cloning.

All admitted native live plans apply affected journal records incrementally.
Queries without `LIMIT` retain the complete matching set under the native
4,096-row limit and the 64 MiB candidate-data budget. Paged queries retain an
ordered prefix, including rows skipped by `OFFSET`, so a deep page does not
require retaining the whole collection. Internal candidate fetches can exceed
the public 1,024-row result-page limit while staying within the native candidate
bound. A depleted prefix or missing replay history triggers one SQL refill at
the requested immutable cut. Ordinary affected updates do not rerun snapshot
SQL. Candidate-limit refusals return a typed error before publishing a revision.

Pending origin polls lease one dispatcher per source. It reads shared journal
records at the emission cadence and notifies only pending views whose registered
dependencies match. Cached responses need no dispatcher. Equivalent readers
continue to share one canonical poll. A missing replay prefix wakes all pending
views so each can take its exact-cut refill path.

The last pending poll releases the worker, cancels queued work, and signals
in-flight replay to stop at its next cancellation check. SQLite calls already
running finish before replay can observe cancellation. Idle sources do no
dispatcher replay work. A later poll starts from its observed source cut and
checks the view before waiting, covering writes during the idle period.
Materialization and frame encoding remain demand-driven. The dispatcher reads
the durable journal and hydrates predecessors from storage; direct dispatch
from old/new extracted values on commit remains separate work.

A minute sweep retires views after 20 minutes without origin activity, covering
the maximum issued capability lifetime. Retirement releases the materializer,
journal registration, host references, and distinct-view quota slot. An
in-flight refresh prevents retirement. Retained snapshot and delta bytes stay
readable; a request beyond the archived log receives `view_retired` and must
reattach. Archived reads do not keep a materializer alive.

Reattachment must reacquire a quota slot. It takes a fresh authorized snapshot
and appends any result change to the existing revision history, preserving old
immutable response bytes. A rejected admission leaves the view archived. After
the archive has spent another retention window without reactivation, the sweep
removes it. The sweep runs without new requests and releases storage outside
the registry lock.

## Result digest

Wire producers and SDKs use the native result-view core's four-lane FNV-1a digest.
An empty result is 32 zero bytes. For each nonempty ordered result:

1. Append the identity's UTF-8 byte length as a little-endian u64, then its bytes.
2. In UTF-8 field-name order, append each name's little-endian u64 byte length,
   its bytes, the value's little-endian u64 byte length, and the opaque JSON bytes.
3. Append `0xff` after each item.
4. For lane `i` from 0 through 3, initialize a u64 hash with
   `0xcbf29ce484222325 XOR (i * 0x9e3779b97f4a7c15)`, wrapping at 64 bits.
   For every input byte, XOR the byte and multiply by `0x100000001b3`, wrapping.
5. Concatenate the four little-endian hashes and encode lowercase hexadecimal.

The HTTP view key includes the digest format version to separate cached bytes
from retired encodings.

## Local SDK qualification

The Rust test harness starts a real TCP member with an isolated database. The SDK
journey uses native fetch and WebSocket, checks canonical/local identity mapping,
empty results, HTTP/WebSocket result equality, and reconnect. Install the SDK's
Node dependencies, then run from the terrabase checkout:

```sh
TERRASCALE_TYPESCRIPT_SDK="$PWD/../ts-typescript" nice -n 19 cargo test --locked \
  -p terrabase-server --lib typescript_browser_websocket_journey -- --ignored
```

The SDK's standalone test is environment-gated; the Rust harness supplies its
isolated endpoint and ephemeral fixture credential. This gate does not require
OCI or Cloudflare credentials.

To test actual browser CORS enforcement, install the SDK's Playwright browsers
and run:

```sh
TERRASCALE_TYPESCRIPT_SDK="$PWD/../ts-typescript" nice -n 19 cargo test --locked \
  -p terrabase-server --lib packed_sdk_cross_origin_browser_journey -- --ignored
```

This harness builds and packs the SDK, serves it on a different local origin,
and runs the Chrome, Firefox, Safari/WebKit, and Edge Playwright projects against
the real member. It checks HTTP and WebSocket updates, reconnect, Region-header
visibility, and readable authentication errors. It does not exercise Cloudflare.

Attach reports `X-TerraScale-Region`; the SDK uses that Region's member host for
optional WebSockets, as required by D-24/D-48. Snapshot and log requests retain
the sync origin. The Snippet accepts only those HTTP paths. This local
qualification does not qualify production DNS, TLS, or Cloudflare deployment.
