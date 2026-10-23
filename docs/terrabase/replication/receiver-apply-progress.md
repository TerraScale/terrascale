# Receiver apply progress

A collection-clear delta can derive more document writes than one transaction's
normal change-group limit. A fixed batch deadline could cancel this work before
commit on every attempt, so replay could never finish.

`AckFrame.applying` (field 4) reports the batch's database/incarnation, last change
id and a strictly growing count of completed join work. The receiver samples the
counter at most once per heartbeat per active batch. Waiting for the writer,
unchanged counters and completed batches emit no progress. State stays within
the existing bounded set of active applications; no response queue is added.

The sender accepts progress only for its exact non-refill batch in flight.
An increase refreshes the existing 15-second inactivity timeout, preserving the
original send timestamp for ACK-lag measurement. Progress never sets response
receipt, advances an ACK, releases a remote-write waiter or records a proof.
Only the normal durable commit and stable checks can produce an ACK.

The wire addition is safe during rolling deployment: old senders ignore the
unknown oneof field; old receivers never emit it. A stalled apply, including one
blocked within a single native statement, still times out after 15 seconds.
Origin transactions remain atomic. There is no storage-format change.
