# Realtime subscriptions (TS-690, first slice)

`GET /projects/{project}/databases/{database}/collections/{collection}/subscribe`
streams the existing shape snapshot and change feed as `text/event-stream`.
The database path parameter is its name. This unversioned route is client TLS only.

Send `Authorization: Bearer <ts-auth access token>`. TerraBase uses its configured
issuer and audience, cached JWKS, signature and access-token type validation.
The signed `project_id` must be a lowercase UUID equal to the route's project;
if `database` is present, it must equal the database name. Missing or mismatched
project grants return `404 unknown_database`. The token must grant `shapes:read`
in its space-separated `scope`, or the route returns `403 forbidden_scope`.
Stored API keys, cookies and query-string tokens do not authenticate subscriptions.
JWT grants authorize access through a verified project/storage binding; catalog memberships alone
do not. Unverified legacy mappings return `404 unknown_database`, following the
[COS verified-binding decision of 2026-10-07](decisions/0011-project-catalog-trust.md).
This route never enrolls a project or changes its binding. Each feed read rechecks the same verified
storage namespace instead of following an unverified project index. Offline validation does not provide immediate
revocation. Delivery stops at signed `exp`, without the authentication clock skew.
Cleanup waits for an in-progress shared feed worker to stop before releasing its
memory reservation; this can delay connection closure after expiration.

Use header-authenticated `fetch` and an SSE parser in browsers; native EventSource
cannot supply the Authorization header. WebSocket delivery and dashboard-owned
plan entitlements are subsequent slices, not implemented by this route yet.

## Selection and resume

- `where`: optional JSON filter using the existing [shape grammar](shape-reads.md).
- `id`: optional document id, combined with `where` using AND.
- `offset`: defaults to `-1`, which automatically reads the initial snapshot.
- `handle`: required with a snapshot/log offset and absent with `-1`.

Every `shape` event contains one existing `ShapeBody`: snapshot pages followed by
filtered, commit-grouped changes, including deletions and documents leaving the
filter. A deletion can name a document the client never received. Treat client
state as consistent only when `up_to_date` is true. Empty live pages are heartbeats
after the feed's 20-second wait. A write to another collection can also advance
the position. Imported replication winners use the same retained history.

```text
event: shape
id: <handle>:<next_offset>
data: {"shape_id":"...","handle":"...","offset":"...","next_offset":"...","up_to_date":true,"changes":[]}

```

Persist the event id only after applying the complete page. Reconnect with the
same `id` and `where` and `Last-Event-ID: <handle>:<next_offset>`, or supply explicit
`handle` and `offset` instead. Do not combine both forms. The shape handle binds
the project, database incarnation, collection generation and filter. It rejects
changes in selection or database/collection recreation. An offset outside retained
history requires a fresh snapshot.

The stream rechecks that the catalog serves this database in the current region
before each feed read and again before delivering its page. Removing this region
from placement ends the stream with `unknown_database`, even while its local copy
still exists.

Errors before the first event use the normal HTTP `ErrorBody`. After streaming
starts, expected feed errors use `event: error` with the same envelope and end the
stream. `must_refetch` means discard the saved view and restart without a handle
at `offset=-1`. Expiry, cancellation, shutdown and failed writes end the stream;
clients must obtain a valid token and reconnect from their last applied event.
Cancellation during an event write aborts the HTTP response so a partial frame
cannot finish successfully. Cancellation during an idle feed read ends with EOF.

## Resource bounds

The first slice has a shared node cap of 128 SSE connections. Each connection
reserves 64 KiB before filter decoding and retains that reservation until it ends.
Before each feed read it reserves another 32 MiB for retained rows, commit framing
and the serialized page, retaining it through delivery. There is one outstanding
page and no subscriber queue. Admission/memory exhaustion returns
`429 subscription_busy` before streaming, or a terminal error event afterwards.
These node safety caps are not plan entitlements or cluster-wide connection limits.

SSE removes insignificant physical CR/LF from validated JSON and writes at most
16 KiB per body chunk; escaped line breaks in string values are preserved. The
pooled framing buffer fits within the connection reservation. Writes and flushes
have a 10-second deadline. Disconnect, expiry, shutdown and
write failures release every reservation and connection slot. No SQLite transaction
is held while waiting on the network. `terrabase.subscriptions.active` on the
`TerraScale.TerraBase` meter reports active SSE connections without tenant labels.
