# Replication incarnation contract

A change ID belongs to one database file incarnation. Copying or restoring a database creates a new incarnation;
the new file may reuse numeric change IDs. An ACK from the old file must never advance the new file's cursor or
release its retained change log, even when that ACK is below the new file's head.

The change-group source returns its store incarnation with the peer's acknowledged position and every successful
group read. Reads, head positions and retention checks require that incarnation and refuse a changed source. ACK
persistence requires the expected incarnation and checks it under the database writer.
The persisted peer ACK position is scoped to that incarnation. A mismatch refuses the ACK without updating peer
bookkeeping or running ACK-driven retention.

Peer replication carries the source incarnation alongside database identity. A batch ACK echoes the sender's
incarnation; it does not name the receiver's own file incarnation. A sender discards cursors from another incarnation.
Each replica has its own file incarnation, even when replicas hold the same logical database.

Digest replies include the source head position read in the same storage transaction as the incarnation and bucket
digests. This identifies one source snapshot; it does not itself certify a rebuild or advance a peer ACK.

Digest discovery returns the current source incarnation; an expected incarnation makes the read conditional.
Bucket reads require the expected source incarnation and echo it on every reply page. Anti-entropy discovers and pins the remote source,
then requests its digest and bucket pages using that incarnation. A refill pins the incarnation named by its sender
and cannot certify a replacement file using the old sender's change position. Missing or changed sources refuse
coverage rather than certifying an empty database.

This changes the internal `terrabase.v1` replication contract and the on-disk peer ACK schema. All peers must run the same
contract. Storage follows the greenfield format policy: the format version is bumped, old data directories are
refused, and no migration or compatibility path is provided. Public HTTP API shapes are unchanged.
