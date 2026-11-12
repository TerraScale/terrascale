---
title: Replication
---

# Replication

A database lives in a set of regions you choose, at least one. Every region of the set accepts reads and writes. At any moment one region of the set orders that database's writes. TerraBase picks it and moves it when the regions change. See [regions](/concepts/regions) to set the set.

## Reads and writes

Reads are served in the region you call. A write sent to another region is forwarded, or committed locally and confirmed.

| Connection | Reads | Writes |
| --- | --- | --- |
| SQL over REST | Served in the region you call | A write sent to another region is forwarded, or committed locally and confirmed |
| psql and Postgres drivers | Reads outside a transaction and read-only transactions stay in the region you call | Other statements run at the region that orders writes |
| SQL over WebSocket | Read-only execute frames stay in the region you call | Other frames run at the region that orders writes |
| Live shapes | Served from the region that orders writes and its edge cache | Use SQL at that region |

A read in the region you call may return an earlier committed state than the ordering region. Postgres and
WebSocket sessions preserve read-your-writes: after a statement runs at the ordering region, later reads use that region until the region you call
catches up. REST reads have no session. A region that has not
completed its first pull sends reads to the ordering region.

On SQL over REST, `TerraScale-Durability: home` acknowledges a local write only after the region that orders writes applied it. The default, `local`, acknowledges that write on the region's quorum. A write the region does not commit locally still runs at the region that orders writes.
The server forwards a timed-out local publish with `TerraBase-Publish-Id` set to the same transaction id, and the ordering region applies that write once.

Two regions writing the same column at nearly the same time are resolved by the greater hybrid logical clock stamp. Each resolved or reverted write is listed in the database's conflicts. See [multi-region writes and conflicts](/guides/multi-region-writes-and-conflicts).

## Recovery and availability

Regions catch up after a restart or a temporary connection failure. A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has. A region that already holds the database can serve SQL reads while
the ordering region is unreachable, using the state it already holds.

TerraBase picks the region that orders writes and moves it when the database's regions change. You do not choose that region.

| Durability | A write is acknowledged when | Lost if the ordering region fails |
| --- | --- | --- |
| Default (`local`) | the region's quorum, for a local write; otherwise the commit at the region that orders writes | at most writes that region has not applied and no other region of the set had pulled yet |
| `TerraScale-Durability: home` | the ordering region applied it | at most the latest writes no other region of the set had pulled yet |
| `"durability": "all"` | every region of the set holds it | nothing |

Set `"durability": "all"` on a SQL REST request to wait for every region of the set. If a region does not
confirm within 10 seconds, the request fails with `503 unavailable`; the write stays at the ordering region.

A copy in another region is not a backup: it follows changes the ordering region applied. Use point-in-time recovery to restore an earlier
state into a new database. A restore needs a free database slot. A rewind restores an earlier state in place: it
replaces every table, index, view and trigger with the state as of a chosen time, in one transaction, keeping the
database's id, name and credentials, and discarding changes committed after that time. See
[backups and restore](/guides/backups-and-restore).

## Encryption and deletion

Database files in every region of the set are encrypted at rest. Deleting a database destroys its backup data keys, making the
retained encrypted backups unreadable. Crypto-shredding and physical removal are separate steps; an individual
SQL row deletion does not destroy a database's backup keys. Independent forks remain available until deleted or expired.

## Lag

There is no published production replication-lag guarantee. Simulation measurements describe their simulated network
and workload; they are not production latency or availability measurements.
