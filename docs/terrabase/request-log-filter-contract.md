# Request-log status filter

`GET /v1/projects/{project}/databases/{database}/logs` accepts one optional
`status` query parameter: `2xx`, `3xx`, `4xx`, or `5xx`. Omission reads all
status classes. Invalid or repeated values return `400 invalid_status`.
The public generated OpenAPI operation `listDatabaseLogs` describes this
existing filter with the same enum and no default value.

The endpoint still requires project authority with `databases:read` and returns
at most the latest 100 recorded requests within 24 hours. Recording is best
effort.

## Cursor reads

`after=<ULID>` turns the read into an export page. The response lists retained
requests whose `id` is after the cursor and that were recorded at least 30
seconds ago (so a flush tick plus catalog replication can deliver remote
regions' requests first), oldest first, and returns `nextCursor`. Pass
`nextCursor` back as `after` on the next read; with no new requests it still
advances to the settled boundary. Start an export from a ULID of its start
time (timestamp, zero randomness).

`gap` is `true` when requests after the cursor may have been discarded unread:
the 100-entry window overflowed past the cursor, or the cursor is older than
the 24-hour retention. `recordingLimited` still reports requests this server
could not record at all. Repeated or non-ULID `after` values return
`400 invalid_cursor`. `status` filters a cursor page's items without changing
`nextCursor`.
