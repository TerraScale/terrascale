# Change-feed cursor scope (2026-10-22)

The `/v1/projects/{project}/databases/{database}/changes` SSE cursor binds the
organization, project, database identity and canonical collection-name set that
issued it. Reordering or repeating collection names preserves the scope. Changing
the database or collection set returns `410 cursor_expired`; start a new feed and
re-read the desired data. An omitted filter differs from an explicit list of all
current collections, because future collections also belong to the omitted filter.

HTTP cursors use envelope marker `2`, a 16-byte scope digest and the existing
marker-`1` per-origin position and prefix proof. Internal shape cursors retain
their existing encoding. The digest detects accidental reuse; it is not a MAC or
an authorization credential. The route still resolves the authenticated tenant
and checks credential reach before consulting the feed or its shared cache.

This intentionally changes the opaque HTTP cursor format. During a rolling
deployment, an old node rejects a new cursor and a new node rejects an old cursor
with `410 cursor_expired`. Clients must handle that response by re-reading; no
cross-version resume promise is made. Peer replication and persisted storage
formats do not change.

Delivery remains at least once. Within a multi-document stamp only its final event
advances the SSE id, so a reconnect can replay preceding events of that stamp.
Deduplicate by `(origin, version, collection, id)`.
