---
title: Replication
---

# Replication

A database has one home region. The home applies writes and is the durable apply point. Replica regions pull changes from the home, keep encrypted copies, and can serve reads they already hold. A replica region can accept a local write; the home applies it. See [regions](/concepts/regions) to set placement.

## Reads and writes

| Connection | Local replica reads | Writes |
| --- | --- | --- |
| SQL over REST | Available once the local replica is ready | A replica region can accept a local write; the home applies it |
| psql and Postgres drivers | Reads outside a transaction and read-only transactions | Run at the home |
| SQL over WebSocket | Read-only execute frames | Run at the home |
| Live shapes | Served from the home and its edge cache | Use SQL at the home |

Replicas are eventually consistent. A local read may return an earlier committed state than the home. Postgres and
WebSocket sessions preserve read-your-writes: after running at the home, later reads use the home until the replica
catches up. REST reads have no session; use the home when a read must see a previous write. A replica that has not
completed its first pull sends reads to the home.

On SQL over REST, `TerraScale-Durability: home` acknowledges a local write only after the home applied it. The default, `local`, acknowledges that write on the replica region's quorum. A write the region does not commit locally still runs at the home.

## Recovery and availability

Replicas catch up after a restart or a temporary connection failure. A ready replica can serve SQL reads while
its home is unreachable, using the state it already holds.

If the home region of a database with replica regions stops responding, the most caught-up replica becomes the
new home within about 15 to 45 seconds, and writes move there. The old home never accepts another write; when it
returns, it follows the new home as a replica. A database without replica regions waits for its home.

| Durability | A write is acknowledged when | Lost if the home region fails |
| --- | --- | --- |
| Default (`local`) | the replica region's quorum, for a local write; otherwise the home commit | at most writes the home has not applied and no replica had pulled yet |
| `TerraScale-Durability: home` | the home applied it | at most the latest writes no replica had pulled yet |
| `"durability": "all"` | every replica region holds it | nothing |

Set `"durability": "all"` on a SQL REST request to wait for every replica region. If a replica region does not
confirm within 10 seconds, the request fails with `503 unavailable`; the write stays at the home.

A replica is not a backup: it follows changes the home applied. Use point-in-time recovery to restore an earlier
state into a new database. A restore needs a free database slot. A rewind restores an earlier state in place: it
replaces every table, index, view and trigger with the state as of a chosen time, in one transaction, keeping the
database's id, name and credentials, and discarding changes committed after that time. See
[backups and restore](/guides/backups-and-restore).

## Encryption and deletion

Database files and replicas are encrypted at rest. Deleting a database destroys its backup data keys, making the
retained encrypted backups unreadable. Crypto-shredding and physical removal are separate steps; an individual
SQL row deletion does not destroy a database's backup keys. Independent forks remain available until deleted or expired.

## Lag

There is no published production replication-lag guarantee. Simulation measurements describe their simulated network
and workload; they are not production latency or availability measurements.
