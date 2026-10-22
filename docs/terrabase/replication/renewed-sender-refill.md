# Refill requests after sender renewal

Issue: [#219](https://github.com/TerraScale/terrascale/issues/219).

A failed or interrupted refill leaves the receiver's database blocked for that
origin, including after the sender's incarnation changes. Rebuilding the sender
can remove its scoped peer ACK rows while retaining every group after zero.
Retention and the missing row alone then cannot tell the sender to refill.

A refused batch now carries `Ack.refill_required` when the receiver remembers
missing coverage and no refill is currently running for that database. The
sender validates the ACK's database and incarnation against its in-flight batch,
keeps its acknowledged position, and requests a refill pinned to its own copy.
Only a committed refill clears the receiver's block. Subsequent live batches
still require their own durable ACK; the refill does not confirm client writes.

Catalog lag, a busy receiver, and a refill already in progress do not request a
new refill. Other databases continue streaming. A reconnect can lose the
sender's in-memory request, but the receiver requests it again on the next
refused batch. An idle sender receives zero stable progress while blocked;
the repair request is triggered by its next batch.

This changes the internal v1 peer ACK contract, with no public HTTP API or
persisted storage-format change. Older sender binaries ignore the new protobuf
field and can retain the stall until upgraded. No migration or format bump is
required.

Fast coverage uses real peer listeners, production CDC and a fresh file from
the rebuild storage path: no peer ACK scope, a retained log, a held refill, then
repair and later live batch convergence. Raw-stream coverage distinguishes the
request from catalog-lag and in-progress-refill refusals.
