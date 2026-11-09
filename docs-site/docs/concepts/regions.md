---
title: Regions
---

# Regions

Choose a home region for writes and other regions for encrypted read replicas.

| Region | ID |
| --- | --- |
| São Paulo | `gru` |
| San Francisco Bay Area | `sjc` |
| Frankfurt | `fra` |

## Place a database

Create a database with a home and an explicit list of replica regions:

```http
POST /v1/projects/{project}/databases
Content-Type: application/json

{"name":"app","homeRegion":"gru","replicaRegions":["sjc","fra"]}
```

Without `homeRegion`, the called region becomes the home. Without `replicaRegions`, the database has no regional
read replicas. The home and every replica must be serving regions; a replica must differ from the home.

Change the replicas with `PATCH /v1/projects/{project}/databases/{database}` and
`{"replicaRegions":["sjc"]}`. An empty list removes the replicas. All-regions placement (`allRegions`, `-all-regions` on
`ts db create` and `ts db placement`) holds a replica in every serving region, including
regions added later; otherwise name each replica region.

## Connect from any region

REST requests and Postgres sessions can reach the database's home from any serving region. The home commits writes.
REST requests containing only reads, psql reads outside a transaction or in a read-only transaction, and read-only
SQL WebSocket frames can use a ready replica in the region receiving the connection. Writes and other transactions
run at the home. Reads before the replica is ready go to the home.

A replica can lag behind the home. Postgres and WebSocket sessions preserve read-your-writes by using the home until
the replica catches up. REST reads are eventually consistent; send them to the home when they must see a previous write.
Replicas are read-only; they do not make writes available when the home is unavailable. See [replication](/concepts/replication).

## Storage and billing

Database files and read replicas are encrypted at rest. Adding a replica places a copy of your data in that region.

The [pricing catalog](https://terrascale.tech/pricing.json) lists one home region per database on every plan.
Writes and stored bytes are metered in the home region. Replica storage is not billed separately. See [pricing](/pricing).
