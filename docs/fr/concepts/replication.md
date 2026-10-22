---
title: Replication and durability
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Replication and durability

Read the durability reported by the serving API literally. The current Documents examples do not establish leaderless operation, global serializability or a multi-region production guarantee.

## Current application contract

Document writes return an opaque version, an offset and a durability label. The candidate's `local_durable` label is an accepting-service acknowledgement. It is not proof that every selected region has persisted the write. Do not parse versions or use a database offset as a portable session token.

A version precondition rejects an update when the observed document version is no longer current at the serving API. It does not establish globally unique application values or cross-region linearizability.

A timeout or lost response can leave a write committed. This contract does not support `Idempotency-Key`; [safe retry guidance](/fr/guides/errors) describes reconciliation.

## Multi-region product requirements

The intended service keeps data within the selected placement and converges authorized replicas without widening residency. Fault recovery, causal sessions, conflict resolution and cross-region durability require independent real-service evidence. The retained isolated CRUD receipt does not qualify them.

Earlier documents described `RegionalDurable` and `GlobalDurable` requirements. Those names are not selectable write modes in the documented current Documents SDK example. Do not assume a current SDK can request them or provides their historical guarantees.

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

In the dashboard, the create and edit database forms have the option "All regions: copies follow new regions automatically". The database page shows an "All regions" badge. The ts-dotnet, ts-js, ts-go and ts-rust SDKs expose the `allRegions` field. See [SDK availability](/fr/guides/sdks).

There is no pricing change. Per-region replica billing, if any, applies as it does for `replicaRegions`. See [pricing](/fr/pricing/).

## Plan an application

Define the consistency and residency requirements of the application before choosing a serving release. Operations that need global uniqueness or an authoritative single ordering need an explicitly supported contract. Keep read-your-writes and failover assumptions out of application logic until the relevant session behavior is verified.

See [SDK availability](/fr/guides/sdks), [live-update compatibility](/fr/concepts/sync) and [pricing](/fr/pricing/) for their separate boundaries.

</div>
