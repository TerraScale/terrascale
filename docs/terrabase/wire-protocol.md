# TerraBase wire protocol v1

A fast path for point document reads and writes without HTTP: one persistent TCP connection per client, length-prefixed
binary frames, and many requests in flight on that connection. It serves the documents of the HTTP Documents API
(`/v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key`) with the same authorization,
admission, validation, metering and durability. It is always on.

- Listener: port `7464` (`wire-port` exists only so tests and a second service on a shared host can move it, like
  `postgres-port`). Plaintext on the node, behind the platform edge, which terminates TLS, as the PostgreSQL listener.
  ts-orch must route the port before clients outside the mesh can use it.
- Server: `src/TerraBase/Features/Wire/WireConnectionHandler.cs`. Test client: `tests/TerraBase.TestHarness/WireClient.cs`.

## Frames

All integers are little-endian.

Request: `u32 length` (of the rest), `u32 request id`, `u8 operation`, body.

Answer: `u32 length` (of the rest), `u32 request id`, `u16 status`, then a head and a body that depend on the status.
Statuses are the HTTP API's (200, 201, 400, 401, 403, 404, 409, 421, 429, 503, ...). Every error answer carries
`u8 code length`, the error code (`not_found`, `invalid_key`, `tenant_busy`, ...) and the message as UTF-8 to the end.

| Operation | Body | Success answer |
| --- | --- | --- |
| `0` hello | `u8 version` (1), then three `u16`-length UTF-8 strings: API key, project ULID, database name | 200, `u8 version` |

Point gets and puts follow (RT3c). Any other operation answers `400 unknown_operation` and the connection stays open. A
frame is at most 1 MiB + 1 KiB (4 KiB before the hello is accepted). A frame out of those bounds answers `400 invalid_frame` with request id 0 and closes
the connection. The memory budget admits each frame before it is read (`429 tenant_memory` with request id 0 otherwise,
which also closes the connection).

## Handshake and authorization

The first frame must be a hello for version 1, sent within 10 seconds; anything else answers `400 unsupported_version` and
closes. The API key is checked as on the HTTP client listener: the key exists, has not expired and grants
`documents:read`; the database is in the key's reach (organization, and project and database when the key is narrowed);
the database is placed in this region (`421 wrong_region` otherwise). A refused hello answers its error and closes.
ts-auth project tokens are not accepted.

The connection keeps its authorization while the system catalog has not committed since it was checked. After any catalog
commit (a revocation, a deletion, a placement change, or any other catalog write, local or replicated) and at the key's
expiry, the next request checks it again from the catalog. A refused check answers that request with its error, reads no
further frame and closes the connection once the requests in flight have answered. A database deleted and created again
under the same name is another database: the connection does not follow it.

## Pipelining and order

Up to 128 requests run at once per connection; the server reads the next frame when an answer has been written. A
request keeps its memory admission (and its answer's) until its answer is written, so a client that stops reading holds
at most 128 admitted answers. Answers are sent as requests complete, so they may arrive out of order: match them by
request id.

## Same admission as the HTTP API

Every request passes the catalog gate (`503 catalog_unverified`), the organization's request lease (`429 tenant_busy`)
and the memory budget, and is recorded as one client request with its bytes, as the HTTP request it replaces. Pressure
signals count it as a client request.

## What an SDK needs

A connection pool, a request id counter, a map of pending requests completed by the reader loop, and reconnection with a
new hello on any closed connection; writes in flight when a connection closes have an unknown outcome and must not be
replayed automatically.
