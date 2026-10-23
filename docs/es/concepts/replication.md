---
title: Replication and durability
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Replication and durability

Each write response tells you how durable the write is. Read that label literally and design for the guarantees below.

## Current application contract

Document writes return an opaque version, an offset and a durability label. `remote_durable` means another region holding the database has also persisted the write. `local_durable` means only the region that accepted the write has persisted it, because no other region holds the database. Do not parse versions or use a database offset as a portable session token.

A version precondition rejects an update when the observed document version is no longer current at the serving API. It does not make application values unique or order writes across regions.

A timeout or lost response can leave a write committed. This contract does not support `Idempotency-Key`; [safe retry guidance](/es/guides/errors) describes reconciliation.

## Multi-region behavior

Data stays within the placement you select, and replicas converge without leaving it. A write waits up to 10 seconds for another region of the placement to confirm it, then answers `remote_durable`, so writes take at least one round trip to the nearest other region. When no other region of the placement is reachable, the write is refused before it runs with `503 remote_region_unavailable` and `Retry-After`. There are no causal sessions or custom conflict resolution.

Earlier documents described `RegionalDurable` and `GlobalDurable` requirements. They are not write modes in the current SDKs; you cannot request them.

## Placement

A database is created in its home region and keeps a full copy in at least one other region:

- `POST /v1/projects/{project}/databases` with `{"name": "main"}` places it in the region that answers and every other serving region. `homeRegion` chooses the home region; it cannot change later.
- `{"name": "main", "replicaRegions": ["fra"]}` names the other regions, up to 8. An empty list answers `400 placement_needs_two_regions`.
- `{"name": "main", "allRegions": true}` follows every serving region; see below.

Change it with `PATCH /v1/projects/{project}/databases/{database}` and `{"replicaRegions": [...]}` or `{"allRegions": true}`. Keep at least one region in `replicaRegions`: a database whose list is empty holds a single copy and refuses every write with `503 remote_region_unavailable` until you add a region back. Send an empty list only right before you delete the database.

Only owners and admins, with the `databases:write` permission, can change placement.

## All regions placement

A database can keep a full copy in every serving region. Use it when the data must be present wherever the service runs.

Set it in one of two ways:

- Create a database: `POST /v1/projects/{project}/databases` with `{"name": "main", "allRegions": true}`.
- Switch an existing database: `PATCH /v1/projects/{project}/databases/{database}` with `{"allRegions": true}`.

To turn it off, send `{"replicaRegions": [...]}` with at least one region, such as `{"replicaRegions": ["sjc"]}`. That sets the region list and clears `allRegions`. Sending `allRegions` and `replicaRegions` in the same request returns `400 placement_conflict`.

What it means:

- Copies follow the regions. A region that joins gets a copy automatically. No API call is needed.
- A region that is removed stops holding and serving the database.
- The 8-region limit applies only to `replicaRegions` lists. All-regions has no limit.
- It works with one region, and it gains copies as regions are added.
- Responses show `"allRegions": true` and `"replicaRegions": []`. The copies follow the regions, not a list.
- It sets placement only. It does not change the durability labels or consistency rules above.

In the dashboard, the create and edit database forms have the option "All regions: copies follow new regions automatically". The database page shows an "All regions" badge. The ts-dotnet, ts-js, ts-go and ts-rust SDKs expose the `allRegions` field. See [SDK availability](/es/guides/sdks).

There is no pricing change. Per-region replica billing, if any, applies as it does for `replicaRegions`. See [pricing](/es/pricing/).

## Delete a database

A database holds copies in other regions, so deleting it takes two calls:

1. Send `PATCH /v1/projects/{project}/databases/{database}` with `{"replicaRegions": []}`. Without this step, the delete returns `409 database_replicated`. From here on the database refuses writes.
2. Delete the database with `DELETE /v1/projects/{project}/databases/{database}`.

## Plan an application

Define the consistency and residency requirements of the application before choosing a serving release. Operations that need global uniqueness or an authoritative single ordering need an explicitly supported contract. Keep read-your-writes and failover assumptions out of application logic until the relevant session behavior is verified.

See [SDK availability](/es/guides/sdks), [live-update compatibility](/es/concepts/sync) and [pricing](/es/pricing/) for their separate boundaries.

</div>
