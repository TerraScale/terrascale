# All-regions placement

A database created or updated with `allRegions: true` holds a full copy in **every serving region**, now and later.
TerraScale's own databases (ts-auth and ts-dashboard, [platform databases](platform-databases.md)) always use it.

## API

- `POST …/projects/{project}/databases` `{"name": "main", "allRegions": true}`.
- `PATCH …/projects/{project}/databases/{database}` `{"allRegions": true}`. Sending `{"replicaRegions": [...]}` turns it
  off and sets that list.
- `allRegions` and `replicaRegions` together answer `400 placement_conflict`.
- Every database body carries `allRegions`. With `allRegions: true`, `replicaRegions` is empty: the copies follow the
  serving regions, not a list.
- Placement changes stay with the database's home region and its owners and admins (scope `databases:write`), as
  for `replicaRegions`. Commits (`create_database`, `set_database_regions`) keep using `replica_regions`.
- A database in every region is replicated, so a delete first needs `PATCH {"replicaRegions": []}`
  (`409 database_replicated` otherwise).

## How it follows the regions

- The catalog record keeps `allRegions` instead of a region list. The catalog itself is placed in every region
  ([multi-region-replication.md](multi-region-replication.md) section 15), so each region reads the record.
- Every placement check (`DatabasePlacement.Includes`, `ManagedDatabase.PlacedIn`) is true for every region. A region
  that ts-orch lists in the peers file ([ts-orch.md](ts-orch.md#peers-file)) is a peer like any other. Its nodes create the local
  file on the first stream or refill, and they serve the database. Nobody calls an API.
- A region that leaves stops being a peer, so nothing streams to it and no write waits for it. Its machines and their
  copies leave with it.
- There is no replica cap: the 8-region cap applies to a `replicaRegions` list only.
- One serving region is valid. Writes follow decision 0013a: in a cell, they are confirmed once a second region serves.

## Billing

This change adds no price. If billing charges per replica region, that applies as it is, and an all-regions database
gains a copy for each region that joins.

## Proof

`tests/TerraBase.Fast/Replication/AllRegionsPlacementTests.cs` covers this. gru creates the database while it is the
only region. sjc and then fra join and receive it with no call: fra also refills writes made before it existed. sjc
leaves, gru stops streaming to it, and later writes still reach fra.
