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
accounted record data, including causal context. Concurrent views reuse the
same immutable record allocations and select only records within their own
snapshot cuts. Byte eviction, excessive backlog, or expired source history
requires a view to rebuild at its exact immutable snapshot; none can become a
silently skipped delta prefix. The source watch detaches when its last owner
is dropped. Interval-based selection of affected views is not yet connected;
the shared publication signal still wakes the views on that source.

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
