# SQL over WebSocket

Producer contract for `TerraBaseSqlSession`, from `docs/terrabase/sql/websocket.md`,
`src/TerraBase/Features/Sql/SqlSocketEndpoint.cs`, `SqlSocket.cs` and `SqlSocketModels.cs` (PIVOT E3, main
at `2020a18ee`). Recorded exchanges: [sql/websocket-session.json](sql/websocket-session.json).

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

An SDK reports `transaction_rolled_back` only after receiving that answer from the server, or after losing an
open transaction for which no committing request was sent. If COMMIT or a request that can commit (including an
autocommit statement or batch) may have left the client and its answer is lost, the outcome is uncertain.
The SDK returns `write_outcome_unknown` (SQLSTATE `08007`) or its existing commit-uncertain error, retains that
failure for every later call on the session, and closes the connection. It never reconnects or automatically
retries an uncertain request. If the client cannot prove no request byte left, it treats the request as sent.
An execute outside a confirmed transaction, or behind unanswered transaction control, is potentially committing.
A lost read or non-committing statement inside a confirmed open transaction retains the transaction-lost behavior.
Only a confirmed refusal or a request proven not sent permits a rollback assertion; reconcile an uncertain write
through another session before deciding whether to repeat it.
