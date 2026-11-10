---
title: Regions
---

# Regions

Choose a home region and replica regions. The home applies writes. Replica regions hold encrypted copies, can serve reads they already hold, and can accept local writes.

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

Without `homeRegion`, the called region becomes the home. Without `replicaRegions`, the database has no replica
regions and waits for its home. The home and every replica must be serving regions; a replica must differ from the home.

Change the replicas with `PATCH /v1/projects/{project}/databases/{database}` and
`{"replicaRegions":["sjc"]}`. An empty list removes the replicas. All-regions placement (`allRegions`, `-all-regions` on
`ts db create` and `ts db placement`) holds a replica in every serving region, including
regions added later; otherwise name each replica region.

## Connect from any region

REST requests and Postgres sessions can reach the database from any serving region. The home applies writes and remains the durable apply point.
REST requests containing only reads, psql reads outside a transaction or in a read-only transaction, and read-only
SQL WebSocket frames can use a ready replica in the region receiving the connection. A replica region can accept a local SQL write; the home applies it. `TerraScale-Durability: home` waits for that apply.
Reads before the replica is ready go to the home.

A replica can lag behind the home. Postgres and WebSocket sessions preserve read-your-writes by using the home until
the replica catches up. REST reads are eventually consistent; send them to the home when they must see a previous write.
A ready replica can serve reads it already holds while the home is unreachable. A database with no replica regions waits for its home. See [replication](/concepts/replication).

## Storage and billing

Database files and replicas are encrypted at rest. Adding a replica places a copy of your data in that region.

The [pricing catalog](https://terrascale.tech/pricing.json) lists one home region per database on every plan.
Writes and stored bytes are metered in the home region. Replica storage is not billed separately. See [pricing](/pricing).
