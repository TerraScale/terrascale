---
title: Regions
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

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
`{"replicaRegions":["sjc"]}`. An empty list removes the replicas. Automatic placement in all current and future
regions (`allRegions`) is coming; name each replica region today.

## Connect from any region

REST requests and Postgres sessions can reach the database's home from any serving region. The home commits writes.
A REST request containing only reads can use a ready replica in the region receiving the request. Reads before the
replica is ready go to the home. Local replica reads over psql and SQL WebSocket are coming.

A replica can lag behind the home. Send reads that must see a previous write to the home region. Replicas are read-only;
they do not make writes available when the home is unavailable. See [replication](/de/concepts/replication).

## Storage and billing

Database files and read replicas are encrypted at rest. Adding a replica places a copy of your data in that region.

The [pricing catalog](https://terrascale.tech/pricing.json) lists one home region per database on every plan.
Writes and stored bytes are metered in the home region. Replica storage is not billed separately. See [pricing](/de/pricing).

</div>
