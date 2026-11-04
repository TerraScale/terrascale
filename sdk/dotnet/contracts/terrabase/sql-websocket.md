# SQL over WebSocket

Producer contract for `TerraBaseSqlSession`, from `docs/terrabase/sql/websocket.md`,
`src/TerraBase/Features/Sql/SqlSocketEndpoint.cs`, `SqlSocket.cs` and `SqlSocketModels.cs` (PIVOT E3, `wrk/e3-sql-ws`
at `8169fa185`). Recorded exchanges: [sql/websocket-session.json](sql/websocket-session.json).

`GET wss://…/v1/projects/{project}/databases/{database}/sql/ws` with `Authorization: Bearer …` and subprotocol
`terrascale.sql.v1`; same scopes as `sql/execute`. Refusals before the upgrade: 400 `websocket_required`, 401, 404,
421 (database served elsewhere). The key is checked before every frame; a revoked key answers an `unauthorized` error
frame and the server closes with 1008.

Client frames (JSON text, at most 1 MiB), each answered in order with its `id`:

| Frame | Meaning |
|---|---|
| `{"id":1,"type":"execute","statements":[{"sql","args","named"}]}` | 1 to 256 statements; one transaction outside a transaction, inside the open one otherwise |
| `{"id":2,"type":"begin"}` | open a transaction (at most 5 seconds) |
| `{"id":3,"type":"commit"}` | commit; after a failed statement it rolls back and answers `transaction_rolled_back` |
| `{"id":4,"type":"rollback"}` | roll back |

Server frames: `{"id","type":"result","results":[…],"transaction":bool}` or
`{"id","type":"error","error":{"code","message","statement","sqlstate"},"transaction":bool}`; `transaction` tells
whether a transaction is open afterwards. After a failed statement inside a transaction every `execute` answers
`transaction_aborted` until `rollback` or `commit`. Close codes: 1003 binary frame, 1009 frame over 1 MiB, 1011
memory budget, 1001 server restarting, 1008 lost authorization. The server pings every 20 seconds.
