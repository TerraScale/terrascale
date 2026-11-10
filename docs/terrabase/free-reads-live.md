# Live shape admission and hints

Home and replica tail reads share one computation per database, granted shape handle,
offset and read mode. A disconnected reader releases only its reference; the wait
continues for the remaining readers. Completed live answers remain available for one
second, within the existing flight and memory bounds. Replica polling belongs to the
shared computation, rather than to each reader.

Handle resolution is shared by database store identity and commit sequence at home,
and by the held database revision on replicas. The cache includes the full query and
bound read grant, holds at most 256 entries, and admits memory before loading. A new
commit or grant binding cannot reuse an older lookup. Bootstrap and epoch-key release
still authenticate each HTTP request; keys remain private and no-store.

## Cache boundaries

Successful live HTTP responses use `private, max-age=1, stale-while-revalidate=1` and
`Vary: Authorization`. Their URLs include the handle, offset and complete filter.
Errors remain no-store. A client delays after an unchanged response to avoid spinning
on a cached empty tail.

Shared CDN caches hold TBS1 ciphertext only. Plaintext live responses cannot safely
use public caching: the current Cloudflare cache rule authenticates neither tokens
nor table grants. The F6 R2 cache rules therefore keep their existing host/path and
query restrictions, status exclusions and immutable ciphertext TTL. A live update's
sealed object uses that same CDN path and verification chain. No public plaintext
cache rule or new permission is required.

## Persistent hints

`GET …/tables/{table}/shape/ws?handle=…&offset=…` upgrades with subprotocol
`terrascale.shape.v1`. Repeat the shape's `where` and `columns`. Authenticate through
an Authorization header or a first JSON text frame `{"type":"auth","token":"…"}`
within five seconds. Never put credentials in the URL. Browser end-user tokens retain
their table grants and actor-bound filters.

The server sends `{"type":"ready"}`, then a `new_data` frame with `handle`, `offset`,
`next_offset` and `manifest`. Hints carry no rows. The manifest contains the existing
sealed-object descriptors; it may be empty while mirroring catches up. Clients apply
only verified HTTP data and retain their last applied offset across disconnects.
Credentials and grants are rechecked before hints and at each 20-second wait deadline.
The existing project-key revocation watch also terminates the socket. A stale handle
sends `must_refetch`; authorization loss terminates access. Socket writes have a
five-second deadline and no unbounded outgoing queue.

## Admission

An organization has at most 1,024 live HTTP requests and hint sockets on one node;
the node admits at most 4,096. Both transports also share the existing eight active
shape computations per organization and 32 per node. Every waiter and connection
reserves memory. These are per-node limits, not a distributed fleet-wide quota.

Existing organization request-rate and short-request concurrency limits apply to
HTTP requests and socket upgrades. A Free organization receives 50 requests/second,
a burst of 100 and four concurrent admission operations; a parked live request releases
its short-request slot. A browser authenticating its first frame receives the same
admission check after its tenant becomes known. A rejected authenticated HTTP request
returns 429, `Retry-After: 1` and a structured error. A connection already upgraded
receives an error frame with `retry_after` and closes.

## JavaScript client

Identical subscriptions on one authenticated client share a stream, bootstrap,
manifest/key cache and socket. Each listener closes independently; the final listener
releases the stream. Distinct transports and filters remain separate. The pool permits
256 distinct shapes and 4,096 listeners per shape.

The server advertises the hint subprotocol in `TB-Shape-Hints`. The client waits on
that socket at the head, offers hinted manifests to its existing CDN verifier and
reads catch-up data over HTTP. A missing mirror falls back to origin HTTP. Broken
sockets reconnect with backoff while HTTP long-polling remains available. Authentication
and grant errors terminate the subscription; `must_refetch` restarts its snapshot.

`terrabase_shape_flights_started_total` counts shared computations, while
`terrabase_shape_flights_active` measures active or briefly retained computations.
HTTP requests, shared computations and CDN object requests must be reported separately:
independent remote clients still need their own authenticated connection and responses.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
