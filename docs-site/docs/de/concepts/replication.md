---
title: Replication
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Replication

A database has one home region that commits writes. Its read replicas pull changes from the home and keep encrypted
read-only copies in the regions you choose. See [regions](/de/concepts/regions) to set placement.

## Reads and writes

| Connection | Local replica reads | Writes |
| --- | --- | --- |
| SQL over REST | Available once the local replica is ready | Routed to the home |
| psql and Postgres drivers | Coming; sessions use the home today | Routed to the home |
| SQL over WebSocket | Coming; sessions use the home today | Run at the home |
| Live shapes | Served from the home and its edge cache | Use SQL at the home |

Replicas are eventually consistent. A local read may return an earlier committed state than the home. Read from the
home for read-your-writes. A replica that has not completed its first pull sends reads to the home.

## Recovery and availability

Replicas catch up after a restart or a temporary connection failure. A ready REST replica can serve reads while
its home is unreachable, using the state it already holds. It cannot accept writes or promise fresh data during
that interruption. There is no automatic failover to a new write region.

A replica is not a backup: it follows changes made at the home. Use point-in-time recovery to restore an earlier
state into a new database. A restore needs a free database slot. In-place point-in-time restore is coming.

## Encryption and deletion

Database files and replicas are encrypted at rest. Deleting a database destroys its backup data keys, making the
retained encrypted backups unreadable. Crypto-shredding and physical removal are separate steps; an individual
SQL row deletion does not destroy a database's backup keys. Independent forks remain available until deleted or expired.

## Lag

There is no published production replication-lag guarantee. Simulation measurements describe their simulated network
and workload; they are not production latency or availability measurements.

</div>
