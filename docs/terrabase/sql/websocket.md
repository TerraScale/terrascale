# SQL over WebSocket

- Status: implemented, 2026-11-03 (PIVOT task E3, with E7 error mapping).
- Code: `src/TerraBase/Features/Sql/SqlSocketEndpoint.cs`, `SqlSocket.cs`, `SqlSocketModels.cs`.
- Proof: `tests/TerraBase.Fast/Sql/SqlSurfaceParityTests.cs` runs one script over REST, the WebSocket and the
  PostgreSQL wire over a first-frame-authenticated socket and requires the same results and errors;
  `SqlSocketAuthTests.cs` covers refused credentials and the 5-second deadline.

## Connecting

`GET wss://api.<domain>/v1/projects/{project}/databases/{database}/sql/ws`. Offer the subprotocol `terrascale.sql.v1`
and the server selects it. A request without an upgrade answers 400 `websocket_required`; a database homed elsewhere
answers 421 like every other data route.

The credential is the same bearer API key or project token as `sql/execute`, with `sql:read` (writes need
`sql:write`). It never goes in the URL: the server reads no query parameter for it. Send it one of two ways:

| Client | How | Refusal |
|---|---|---|
| Servers, CLIs, native SDKs | `Authorization: Bearer <key>` on the upgrade request | 401 `invalid_key` or 403 `forbidden_scope` before the upgrade |
| Browsers (they cannot set WebSocket headers) | Upgrade without `Authorization`, then send `{"id":1,"type":"auth","token":"<key>"}` as the first frame within 5 seconds. The server answers `{"id":1,"type":"result","results":[],"transaction":false}` | an `unauthorized`, `forbidden_scope`, `wrong_region` or `unavailable` error frame, then close 1008. No auth frame within 5 seconds, or any other first frame, also closes with 1008 |

Platform services such as ts-auth reach their databases on the internal listener with the peer credential
(`X-TerraBase-Peer-Credential`) instead of a bearer key, as on `sql/execute`. That listener speaks only HTTP/2, so
their upgrade is an extended CONNECT (RFC 8441); the socket needs no auth frame and runs with the platform operator's
access to the project's database, checked again before every frame like any other socket.

Any node of the region accepts the socket on either listener. A node that is not the region's writer relays the whole
socket to the writer, which runs the session ([regional writer](../regional-rollout-handoff.md#requests-that-reach-the-serving-peer)).

The auth frame holds at most 16 KiB, and nothing else is read before it. A frame-authenticated socket gets the same
checks as a header: the project token's key admission and revocation watch, IP allowlists, project and database
restrictions, scopes and the database's placement (a database served elsewhere answers `wrong_region`, 421/08004,
naming its home region). A second `auth` frame answers `invalid_request`.

Either way, the credential is checked again before every frame. A revoked key, a lost scope or a deleted database
answers an `unauthorized` error frame and closes the socket with 1008; a revoked project token ends it as soon as
ts-auth reports the revocation: a frame-authenticated socket closes with 1008, a header-authenticated one is aborted.

## Frames

Frames are JSON text of at most 1 MiB. The server answers each frame in order, one answer per frame, echoing `id`.

| Client frame | Meaning |
|---|---|
| `{"id":1,"type":"execute","statements":[{"sql":"…","args":[…],"named":{…}}]}` | Run 1 to 256 statements, with arguments as on `sql/execute`. Outside a transaction they run as one transaction, all or nothing. Inside one they run in it. |
| `{"id":2,"type":"begin"}` | Open a transaction (at most 5 seconds). |
| `{"id":3,"type":"commit"}` | Commit it. After a failed statement, commit rolls back and answers `transaction_rolled_back`. |
| `{"id":4,"type":"rollback"}` | Roll it back. |

`BEGIN`, `COMMIT` and `ROLLBACK` as statement text are refused with `transaction_state`; use the frames. `SAVEPOINT`,
`RELEASE` and `ROLLBACK TO` are accepted as statements.

| Server frame | Meaning |
|---|---|
| `{"id":1,"type":"result","results":[{"columns":[{"name","decltype"}],"rows":[[…]],"rows_affected":0,"last_insert_rowid":null}],"transaction":false}` | Success. `results` holds one entry per statement, empty for `begin`, `commit` and `rollback`. |
| `{"id":1,"type":"error","error":{"code":"unique_violation","message":"…","statement":0,"sqlstate":"23505"},"transaction":true}` | Failure, with the same code, message and SQLSTATE as `sql/execute` and the PostgreSQL wire ([codes](native-sql.md#values-errors-and-metering)). |

`transaction` tells whether a transaction is still open after the frame. Values are null, numbers, strings and blobs
as `{"base64":"…"}`, as on `sql/execute`.

After a failed statement inside a transaction, every `execute` answers `transaction_aborted` (25P02) until
`rollback` or `commit`, matching the PostgreSQL wire.

## Limits and closing

| Event | Answer |
|---|---|
| Unreadable JSON, unknown `type`, a second `auth`, 0 or more than 256 statements | `invalid_request` error frame; the socket stays open. |
| Organization request budget exhausted | `tenant_busy` error frame; retry. |
| Binary frame | close 1003 |
| Frame over 1 MiB, or an auth frame over 16 KiB | close 1009 |
| Organization memory budget exhausted while reading a frame | close 1011 |
| No auth frame within 5 seconds of a headerless upgrade | close 1008 |
| Server restarting | close 1001 |

The server pings every 20 seconds and drops a peer that does not answer within 20 seconds. Every frame counts as one
request in usage and request logs, with its received and sent bytes.
