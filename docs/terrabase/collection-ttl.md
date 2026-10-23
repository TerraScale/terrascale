# Collection TTL

A collection can declare one expiry member. Documents whose member holds a past time are
deleted by the database's home region, so sessions, carts, one-time codes and caches need
no cron job. These routes are on the client TLS listener:

| Method | Route | Scope | Behavior |
| --- | --- | --- | --- |
| PUT | `/v1/projects/{project}/databases/{database}/collections/{collection}/ttl` | `documents:write` | Declare or replace; body `{"path":"/expiresAt"}` |
| GET | Same route | `documents:read` | `200 {"path":"/expiresAt"}`, or `{"path":null}` when none |
| DELETE | Same route | `documents:write` | Clear; documents stop expiring |

`path` is an RFC 6901 JSON Pointer of 1 to 16 object members; anything else is
`400 invalid_ttl`. An unknown collection is `404 unknown_collection`.

## What expires

A document expires when the member holds an RFC 3339 time in UTC, such as
`"2026-10-22T12:00:00Z"` or `"2026-10-22T12:00:00.250Z"`, earlier than now. Documents
without the member, or with a number, `null` or any other value, never expire. Write UTC
(`Z`): a time with another offset is found only once the UTC clock passes its local text.

## Sweep and lag

Every hour, each node sweeps the databases whose home region is its own
(`CollectionTtlSweeper`). Each expired document is deleted through the normal write path,
conditional on the version the sweep read, so a document rewritten after the read keeps
its new version. The delete is an ordinary tombstone: it replicates to every placed
region, appears in `/changes` as `op: "delete"`, and follows the usual tombstone GC rules.
Sweep deletes count as writes for usage; the sweep's reads do not.

The documented lag is **up to about one hour** after the expiry time, longer while the
home region is down. Until the sweep, expired documents stay readable and match queries;
filter on the member to hide them earlier.

The sweep queries the member with a filter. Index the member in large collections so a
pass stays inside the query budget; a database whose pass fails is retried next hour.

## Storage

The path is the `ttl_path` member of the collection's replicated metadata document, next
to `schema`. No storage format change.
