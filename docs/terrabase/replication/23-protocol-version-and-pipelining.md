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

Sender (`PeerStreams`):

- Each cursor keeps its batches in flight oldest first (`Flight`). The next batch follows the newest one in flight
  (`Batch.after` = its last change id), or the peer's ack when none is in flight. `after` and the window go only on
  a protocol 2 link; a protocol 1 link keeps one batch per database in flight and sends no `after`.
- Every answer is the oldest flight's: the peer answers a database's batches in arrival order, and an applied ack
  names its batch's change id. Each batch in flight has its own deadline: `ProgressTimeout` from when it went or, as
  the peer applies a database's batches one after another, from the answer to the batch before it, or from the peer's
  last `Applying` report of work on that batch ([receiver apply progress](receiver-apply-progress.md)); the receiver
  reports every batch in progress, and a report refreshes only the batch it names. An answer read off the stream meets
  the deadline of the oldest batch not answered yet, never of a later one.
- An applied ack must answer the oldest batch in flight and moves the cursor's ack to it: acks stay cumulative and
  a client write is confirmed only once the peer holds it durably, as before.
- A refusal dooms the batches after it (the receiver refuses them too); an applied ack for a doomed batch ends the
  stream. The database sends nothing more until every batch in flight answered, then resends from the ack. One
  refusal backs off once, not once per batch it doomed; a refusal asking for a refill retries at once (from the ack).
- Frontiers and refills go only with nothing in flight: a frontier claims every group below it held.
- A refused batch retries after 100 ms, doubling to 10 s, each wait jittered to 50-100 % (before: a fixed 5 s). A
  refused refill keeps the fixed 5 s retry. The link wakes for the earliest retry (`ShippingWork.Later`). The reconnect backoff is unchanged (1 s doubling to 30 s).
- A ship pass queues the database again only for a full page (more is waiting) or a pass it skipped (window full,
  rewinding, refill in flight): commits while batches fly queue it themselves.

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
