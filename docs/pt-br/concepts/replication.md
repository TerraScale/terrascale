---
title: Replication and durability
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Replication and durability

Each write response tells you how durable the write is. Read that label literally and design for the guarantees below.

## Current application contract

Document writes return an opaque version, an offset and a durability label. `local_durable` means the region that accepted the write has persisted it; other regions receive it afterwards. Do not parse versions or use a database offset as a portable session token.

A version precondition rejects an update when the observed document version is no longer current at the serving API. It does not make application values unique or order writes across regions.

A timeout or lost response can leave a write committed. This contract does not support `Idempotency-Key`; [safe retry guidance](/pt-br/guides/errors) describes reconciliation.

## Multi-region behavior

Data stays within the placement you select, and replicas converge without leaving it. Writes do not yet wait for other regions, and there are no causal sessions or custom conflict resolution.

Earlier documents described `RegionalDurable` and `GlobalDurable` requirements. They are not write modes in the current SDKs; you cannot request them.

## All regions placement

A database can keep a full copy in every serving region. Use it when the data must be present wherever the service runs.

Set it in one of two ways:

- Create a database: `POST /v1/projects/{project}/databases` with `{"name": "main", "allRegions": true}`.
- Switch an existing database: `PATCH /v1/projects/{project}/databases/{database}` with `{"allRegions": true}`.

To turn it off, send `{"replicaRegions": [...]}`. That sets the region list and clears `allRegions`. Sending `allRegions` and `replicaRegions` in the same request returns `400 placement_conflict`.

What it means:

- Copies follow the regions. A region that joins gets a copy automatically. No API call is needed.
- A region that is removed stops holding and serving the database.
- The 8-region limit applies only to `replicaRegions` lists. All-regions has no limit.
- It works with one region, and it gains copies as regions are added.
- Responses show `"allRegions": true` and `"replicaRegions": []`. The copies follow the regions, not a list.
- It sets placement only. It does not change the durability labels or consistency rules above.

To delete an all-regions database:

1. Send `PATCH /v1/projects/{project}/databases/{database}` with `{"replicaRegions": []}`. Without this step, the delete returns `409 database_replicated`.
2. Delete the database.

Only owners and admins, with the `databases:write` permission, can change placement.

In the dashboard, the create and edit database forms have the option "All regions: copies follow new regions automatically". The database page shows an "All regions" badge. The ts-dotnet, ts-js, ts-go and ts-rust SDKs expose the `allRegions` field. See [SDK availability](/pt-br/guides/sdks).

There is no pricing change. Per-region replica billing, if any, applies as it does for `replicaRegions`. See [pricing](/pt-br/pricing/).

## Plan an application

Define the consistency and residency requirements of the application before choosing a serving release. Operations that need global uniqueness or an authoritative single ordering need an explicitly supported contract. Keep read-your-writes and failover assumptions out of application logic until the relevant session behavior is verified.

See [SDK availability](/pt-br/guides/sdks), [live-update compatibility](/pt-br/concepts/sync) and [pricing](/pt-br/pricing/) for their separate boundaries.

</div>
