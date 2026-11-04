# Database regions

Each database is served by one region, its **home region**. TerraBase does not copy a database to another region and
does not replicate writes between regions. A write is acknowledged after the home region's local commit
([single-region decision](decisions/2026-10-29-single-region-until-sync.md)).

Replica regions on Turso sync (R3) are being built in another thread; until they ship, everything below holds.

The heads/pull WAN protocol, its metrics, the partition suites and `scripts/terrabase/cross-region-check.py` were
removed with the replication engine. The former design is in the [archive](archive/README.md).

## Placement

A database record carries a placement (`DatabasePlacement` in `Tenancy/ITenantDirectory.cs`): `Home`, `Replicas`,
`Version` and `AllRegions`. On `main`, `Replicas` is always empty and `AllRegions` is always false.

- **Create.** `homeRegion` defaults to the called region. A region TerraBase does not serve, or a value that is not a
  region, answers `400 invalid_request`; the message lists the serving regions for the former. Another serving region answers
  `421 wrong_region` with `ts-replay: region=<home>`, so the edge replays the create there.
- **Replicas refused.** A create or `PATCH` with a non-empty `replicaRegions` or `allRegions: true` answers
  `400 replicas_unavailable` and changes nothing.
- **Update.** `PATCH` renames the database or confirms its unchanged home-only placement. `homeRegion` cannot change.
- **Home-only operations.** Database-scoped Management requests other than reading, updating and deleting the record
  are checked against the home region (`DatabaseRegions.ElsewhereAsync`).

## Wrong region

`UseTerraBaseDatabasePlacement` checks every client-listener request that names a project and a database. When this
region holds the database's record and the placement does not include this region, it answers `421 wrong_region` with:

- `X-TerraBase-Database-Home`: the home region;
- `X-TerraBase-Database-Regions`: the serving regions, home first;
- `Cache-Control: no-store`;
- `ts-replay: region=<regions>` behind the edge ([ts-orch.md](ts-orch.md#ts-replay)).

TerraBase never proxies or forwards the request. A region's catalog holds the databases homed there
([SQL catalog](sql-catalog.md#regions)), so a region without the record answers as for any unknown database.

## Region discovery

Serving regions come from `TS_REGION` and the ts-orch peers file `TS_PEERS_FILE` ([ts-orch.md](ts-orch.md)). Peers
are used for routing and private listener binding; discovering a region grants no placement and creates no copy.

## Tests

Fast covers placement and its refusals in `tests/TerraBase.Fast/Management/RegionCatalogTests.cs` (including
`replicas_unavailable`) and `DatabaseRegionTests.cs` (the reported region across restarts).
