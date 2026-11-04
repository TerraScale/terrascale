---
title: Replication
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Replication

A database has one home region that commits writes. Its read replicas pull changes from the home and keep encrypted
read-only copies in the regions you choose. See [regions](/hi/concepts/regions) to set placement.

## Reads and writes

| Connection | Local replica reads | Writes |
| --- | --- | --- |
| SQL over REST | Available once the local replica is ready | Routed to the home |
| psql and Postgres drivers | Reads outside a transaction and read-only transactions | Routed to the home |
| SQL over WebSocket | Read-only execute frames | Run at the home |
| Live shapes | Served from the home and its edge cache | Use SQL at the home |

Replicas are eventually consistent. A local read may return an earlier committed state than the home. Postgres and
WebSocket sessions preserve read-your-writes: after running at the home, later reads use the home until the replica
catches up. REST reads have no session; use the home when a read must see a previous write. A replica that has not
completed its first pull sends reads to the home. Transactions that are not read-only run at the home.

## Recovery and availability

Replicas catch up after a restart or a temporary connection failure. A ready replica can serve SQL reads while
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
