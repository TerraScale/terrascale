---
title: Replication and durability
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Replication and durability

Each write response tells you how durable the write is. Read that label literally and design for the guarantees below.

## Current application contract

Document writes return an opaque version, an offset and a durability label. `remote_durable` means another region holding the database has also persisted the write. `local_durable` confirms persistence in the region that accepted the write; replication to the other placed regions continues in the background. It does not confirm a second regional copy. Do not parse versions or use a database offset as a portable session token.

A version precondition rejects an update when the observed document version is no longer current at the serving API. It does not make application values unique or order writes across regions.

A timeout or lost response can leave a write committed. This contract does not support `Idempotency-Key`; [safe retry guidance](/zh-cn/guides/errors) describes reconciliation.

## Multi-region behavior

Data stays within the placement you select, and replicas converge without leaving it. Document writes default to `local_durable`. To wait for another placed region to persist a write, send `TB-Durability: remote_durable`; confirmation can take up to 10 seconds and at least one network round trip. If no other placed region is reachable, this requested confirmation is refused before execution with `503 remote_region_unavailable` and `Retry-After`. A lost acknowledgement can leave the outcome unknown; follow [safe retry guidance](/zh-cn/guides/errors). There are no causal sessions or custom conflict resolution.

Earlier documents described `RegionalDurable` and `GlobalDurable` requirements. They are not write modes in the current SDKs; you cannot request them.

## Placement

A writable database requires at least two regions: its home region and at least one other region holding a full copy. Single-region databases are not supported. By default, creation uses every currently serving region:

- `POST /v1/projects/{project}/databases` with `{"name": "main"}` places it in the region that answers and every other currently serving region. `homeRegion` chooses the home region; send the request to that region. It cannot change later. Omitting `replicaRegions` does not mean a single-region database, and does not follow regions added later.
- `{"name": "main", "replicaRegions": ["fra"]}` names the other regions, up to 8. An empty list answers `400 placement_needs_two_regions`.
- `{"name": "main", "allRegions": true}` follows every serving region; see below.

Change it with `PATCH /v1/projects/{project}/databases/{database}` and `{"replicaRegions": [...]}` or `{"allRegions": true}`. Keep at least one region besides home in `replicaRegions`. An empty explicit list is only a teardown step immediately before deletion: document writes then fail with `409 placement_needs_two_regions` until you add a region back. It is not a supported single-region configuration.

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
- It follows serving regions automatically; it does not provide a writable single-region mode.
- Responses show `"allRegions": true` and `"replicaRegions": []`. The copies follow the regions, not a list.
- It sets placement only. It does not change the durability labels or consistency rules above.

In the dashboard, creation defaults to all currently serving regions. An explicit selection must contain at least two regions. The create and edit forms also have the option "All regions: copies follow new regions automatically" to include future regions. The database page shows an "All regions" badge. The ts-dotnet, ts-js, ts-go and ts-rust SDKs expose the `allRegions` field. See [SDK availability](/zh-cn/guides/sdks).

There is no pricing change. Per-region replica billing, if any, applies as it does for `replicaRegions`. See [pricing](/zh-cn/pricing/).

## Delete a database

A database holds copies in other regions, so deleting it takes two calls:

1. Send `PATCH /v1/projects/{project}/databases/{database}` with `{"replicaRegions": []}`. Without this step, the delete returns `409 database_replicated`. From here on document writes fail with `409 placement_needs_two_regions`; delete immediately rather than using this as a single-region configuration.
2. Delete the database with `DELETE /v1/projects/{project}/databases/{database}`.

## Plan an application

Define the consistency and residency requirements of the application before choosing a serving release. Operations that need global uniqueness or an authoritative single ordering need an explicitly supported contract. Keep read-your-writes and failover assumptions out of application logic until the relevant session behavior is verified.

See [SDK availability](/zh-cn/guides/sdks), [live-update compatibility](/zh-cn/concepts/sync) and [pricing](/zh-cn/pricing/) for their separate boundaries.

</div>
