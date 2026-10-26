---
title: Replication and durability
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Replication and durability

Each write response tells you how durable the write is. Read that label literally and design for the guarantees below.

## Current application contract

Document writes return an opaque version, an offset and a durability label. `remote_durable` means another region holding the database has also persisted the write. `local_durable` confirms persistence in the region that accepted the write; replication to the other placed regions continues in the background. It does not confirm a second regional copy. Do not parse versions or use a database offset as a portable session token.

A version precondition rejects an update when the observed document version is no longer current at the serving API. It does not make application values unique or order writes across regions.

A timeout or lost response can leave a write committed. This contract does not support `Idempotency-Key`; [safe retry guidance](/pt-br/guides/errors) describes reconciliation.

## Multi-region behavior

Live database copies stay within the placement you select, and replicas converge without leaving it. Encrypted backups have separate [residency terms](#residency-and-backups). Document writes default to `local_durable`. To wait for another placed region to persist a write, send `TB-Durability: remote_durable`; confirmation can take up to 10 seconds and at least one network round trip. If no other placed region is reachable, this requested confirmation is refused before execution with `503 remote_region_unavailable` and `Retry-After`. A lost acknowledgement can leave the outcome unknown; follow [safe retry guidance](/pt-br/guides/errors). There are no causal sessions or custom conflict resolution.

Earlier documents described `RegionalDurable` and `GlobalDurable` requirements. They are not write modes in the current SDKs; you cannot request them.

## Discover and route to regions

Discover the currently serving region IDs without credentials:

```bash
curl -sS --fail-with-body 'https://api.terrascale.tech/v1/regions'
```

```json
{"regions":["fra","gru","sjc"]}
```

The catalog is cached for five seconds. It lists service availability, not your database's placement or your plan's allowance. Read an authorized database's `homeRegion`, `replicaRegions` and `allRegions` fields to determine its placement. For `allRegions: true`, an empty `replicaRegions` list means the catalog determines the copies.

Use `https://api.terrascale.tech` for normal routing to a region that serves the database. `https://<region>.api.terrascale.tech` selects a regional entrypoint. The response's `X-TerraBase-Region` identifies the region that actually answered; a hostname alone is not evidence of where a request ran.

If a request must execute in one region, set `X-TerraScale-Region-Pin` to that region ID. This read-only Bash example assumes the [quickstart's environment and access token](/pt-br/getting-started/); choose a region in your database's placement:

```bash
export TERRASCALE_REGION='gru'
curl -sS --fail-with-body --include \
  -H @<(printf 'Authorization: Bearer %s\nX-TerraScale-Region-Pin: %s\n' "$TERRASCALE_ACCESS_TOKEN" "$TERRASCALE_REGION") \
  "https://$TERRASCALE_REGION.api.terrascale.tech/v1/projects/$TERRASCALE_PROJECT_ID/databases/$TERRASCALE_DATABASE_NAME/collections"
```

A pin controls request routing; it does not move data or change placement. An unsatisfied or malformed pin can return `421 misdirected_request`. A database placement or home-region refusal returns `421 wrong_region`, with `X-TerraBase-Database-Home` and `X-TerraBase-Database-Regions` when available. See [421 recovery](/pt-br/guides/errors#region-routing-errors-421); do not remove a required pin or retry a write blindly.

## Placement

A writable database requires at least two regions: its home region and at least one other region holding a full copy. Single-region databases are not supported. By default, creation uses every currently serving region:

- `POST /v1/projects/{project}/databases` with `{"name": "main"}` places it in the region that answers and every other currently serving region. `homeRegion` chooses the home region; send the request to that region. It cannot change later. Omitting `replicaRegions` does not mean a single-region database, and does not follow regions added later.
- `{"name": "main", "replicaRegions": ["fra"]}` names the other regions, up to 8. An empty list answers `400 placement_needs_two_regions`.
- `{"name": "main", "allRegions": true}` follows every serving region; see below.

Change it with `PATCH /v1/projects/{project}/databases/{database}` and `{"replicaRegions": [...]}` or `{"allRegions": true}`. Keep at least one region besides home in `replicaRegions` for a writable database. Updating to an empty explicit list makes document writes fail with `409 placement_needs_two_regions` until you add a region back. It is not a supported single-region configuration and is not required before deletion.

Only owners and admins, with the `databases:write` permission, can change placement.

### Availability, plan allowance and request limits

Current [plans](/pt-br/pricing/) allow up to three regions per database, matching the three regions currently offered. A writable database needs at least two of them. The API's structural bound of eight entries in `replicaRegions` counts regions **besides** home; it does not entitle a plan to nine regions or make undiscovered regions available. `allRegions` follows the serving catalog rather than an eight-entry list. Use an explicit list when the allowed locations must stay fixed.

## All regions placement

A database can keep a full copy in every serving region. Use it when the data must be present wherever the service runs.

Set it in one of two ways:

- Create a database: `POST /v1/projects/{project}/databases` with `{"name": "main", "allRegions": true}`.
- Switch an existing database: `PATCH /v1/projects/{project}/databases/{database}` with `{"allRegions": true}`.

To turn it off, send `{"replicaRegions": [...]}` with at least one region, such as `{"replicaRegions": ["sjc"]}`. That sets the region list and clears `allRegions`. Sending `allRegions` and `replicaRegions` in the same request returns `400 placement_conflict`.

What it means:

- Copies follow the regions. A region that joins gets a copy automatically. No API call is needed.
- A region that is removed stops holding and serving the database.
- The eight-entry request limit applies only to explicit `replicaRegions` lists. All-regions follows the serving catalog; the availability and plan allowances above still describe the offered regions.
- It follows serving regions automatically; it does not provide a writable single-region mode.
- Responses show `"allRegions": true` and `"replicaRegions": []`. The copies follow the regions, not a list.
- It sets placement only. It does not change the durability labels or consistency rules above.

In the dashboard, creation defaults to all currently serving regions. An explicit selection must contain at least two regions. The create and edit forms also have the option "All regions: copies follow new regions automatically" to include future regions. The database page shows an "All regions" badge. The ts-dotnet, ts-js, ts-go and ts-rust SDKs expose the `allRegions` field. See [SDK availability](/pt-br/guides/sdks).

Both placement modes use the same [pricing](/pt-br/pricing/): storage counts once in each selected region, writes count once regardless of replica count, and replication traffic is not billed. Following a newly serving region adds another stored copy.

## Delete a database

Send `DELETE /v1/projects/{project}/databases/{database}` to the home region. No preceding placement update is required, including for all-regions databases. A request to another region returns `421 wrong_region`.

A `204` response records logical deletion and local cleanup; remote erasure is retried durably and may finish later. Independent branches remain separate databases and are not deleted with their source.

## Residency and backups

Explicit placement selects the locations of live database copies. `allRegions` includes regions added later, so it is unsuitable for a fixed-country requirement unless every future location is acceptable.

Encrypted backups and object storage have separate locations: [DPA Annex 3](/pt-br/legal/dpa#annex-3-authorized-subprocessors) and the [privacy notice](/pt-br/legal/privacy) list Tigris processing in the United States and the region nearest the database. Selecting only European live replicas does not promise European-only backup storage.

A [branch](/pt-br/guides/branches) or [restore](/pt-br/guides/backups-and-restore#placement-and-residency) creates another database and consumes the same organization allowance. Do not assume it preserves the source's placement. Confirm the creation policy is acceptable before copying sensitive data, then inspect the returned placement and verify the target before directing application traffic to it. A creation response alone does not establish that every regional copy has caught up.

## Plan an application

Define the consistency and residency requirements of the application before choosing a serving release. Operations that need global uniqueness or an authoritative single ordering need an explicitly supported contract. Keep read-your-writes and failover assumptions out of application logic until the relevant session behavior is verified.

See [SDK availability](/pt-br/guides/sdks), [live-update compatibility](/pt-br/concepts/sync) and [pricing](/pt-br/pricing/) for their separate boundaries.

</div>
