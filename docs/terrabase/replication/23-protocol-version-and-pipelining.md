# 23. Peer stream protocol version and pipelined batches (RT2 lane I)

Base: the `Push` stream of [8. Transport](08-transport.md), as built in `PeerStreams` (sender) and `PeerStreamReceiver`.

## Version negotiation

- `Hello.protocol` (field 5) and `Welcome.protocol` (field 2) carry the newest protocol each side speaks
  (`ReplicationProtocol.Version`). A build from before the fields sends neither, which reads as 0 and means protocol 1.
- A stream uses the lower of the two (`ReplicationProtocol.Negotiate`). The sender keeps it on the link and sends only
  what that protocol has.
- A receiver ignores a `PushFrame` whose kind it does not know (`KindCase.None`) and keeps the stream. A sender already
  ignored an `AckFrame` of an unknown kind. So a newer frame kind never ends an older peer's stream.

| Version | Adds |
| --- | --- |
| 1 | The stream as before the fields: one batch per database in flight. |
| 2 | Up to `ReplicationProtocol.Window` (8) batches per database in flight, chained by `Batch.after` (field 5). |

## Pipelined batches (protocol 2)

Why: with one batch in flight, a commit that lands while its database's batch waits for the remote ack waits a whole
round trip plus the remote apply before it even ships. On gru ↔ fra (RTT ~188 ms) that alone breaks a 200 ms p99.

Sender (`PeerStreams`): every batch names in `Batch.after` the ack it follows; one batch per database is in flight
(a chain of one is valid protocol 2).

Receiver (`PeerStreamReceiver`), for a sender whose Hello names protocol 2, per database on the stream (`Chain`):

- A batch is admitted only if `after` equals the last admitted batch's `last_change_id` (the first batch of a stream,
  or of another sender copy, starts the chain at its own `after`), at most `Window` are in progress, and the stream's
  database bound holds. A batch of another copy that arrives while the last copy's batches are in progress is refused
  and starts its copy's chain broken at its `after`: only a resend from there restarts it.
- An admitted batch applies after the one it follows, and is refused if that one was refused: nothing applies past a
  gap, so an ack covers every batch before it.
- After any refusal the chain is broken: every batch is refused until none of the database is in progress and one
  follows the last applied batch again (the sender's resend from its ack).
- A database's answers go in the order its batches arrived: a refusal waits for the batches of the database in
  progress before it, and an answer that completes before an older one of its database is written after it, so the
  sender reads every answer as its oldest batch's. A refill that commits restarts the chain at its change id.

## Rolling deploy

Additive proto fields only; no wire or storage format change. An old receiver ignores `Hello.protocol` and welcomes
without a version, so a new sender uses protocol 1 with it: one batch per database in flight, as before. An old sender
sends no version and gets protocol 1 behaviour from a new receiver (a second batch of a database in progress is
refused at once). Refused acks keep their form (no change id). No format bump.

Tests: `ProtocolVersionTests`, `PipelinedBatchTests` (tests/TerraBase.Fast/Replication).
