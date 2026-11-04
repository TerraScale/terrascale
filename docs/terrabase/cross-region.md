# Database regions

Each database is served by one region, its **home region**. TerraBase does not copy a database to another region and
does not replicate writes between regions. A write is acknowledged after the home region's local commit
([single-region decision](decisions/2026-10-29-single-region-until-sync.md)).

The heads/pull WAN protocol, its metrics, the partition suites and `scripts/terrabase/cross-region-check.py` were
removed with the replication engine. The former design is in the [archive](archive/README.md).

## Placement

A database record carries a placement (`DatabasePlacement` in `Tenancy/ITenantDirectory.cs`): `Home`, `Replicas`,
`Version` and `AllRegions`. Replica regions hold read replicas ([region replicas](region-replicas.md)); the placement API
and moving the home are in [placement](placement.md).

- **Create.** `homeRegion` defaults to the called region. A region TerraBase does not serve, or a value that is not a
  region, answers `400 invalid_request`; the message lists the serving regions for the former. Another serving region answers
  `421 wrong_region` with `ts-replay: region=<home>`, so the edge replays the create there.
- **Replicas.** A create or `PATCH` names `replicaRegions` or `allRegions: true` ([placement](placement.md)).
- **Update.** `PATCH` renames the database or sets its replica regions. `homeRegion` changes only through
  `POST …/move-home`.
- **Home-only operations.** Database-scoped Management requests other than reading, updating and deleting the record
  are checked against the home region (`DatabaseRegions.ElsewhereAsync`).

## Wrong region

`UseTerraBaseDatabasePlacement` checks every client-listener request that names a project and a database. When this
region holds the database's record and the placement does not include this region, it answers `421 wrong_region` with:

- `X-TerraBase-Database-Home`: the home region;
- `X-TerraBase-Database-Regions`: the serving regions, home first;
- `Cache-Control: no-store`;
- `ts-replay: region=<regions>` behind the edge ([ts-orch.md](ts-orch.md#ts-replay)).

A region's catalog holds the databases homed there ([SQL catalog](sql-catalog.md#regions)). A region without the record
forwards the request to the database's home instead (below).

## Forwarding to the home

Any region serves any database: a request that lands in a region without the record reaches the home over the private
mesh.

- **REST.** After authentication, a project request (`/v1/projects/{project}/...` with a project token) whose project, or
  named database, is not in this region's catalog asks the other regions' nodes on the internal listener
  (`GET /v1/internal/home`, peer credential) which one holds it, caches the answer for 30 seconds and forwards the request
  there over HTTP/2 with the peer credential and `X-TerraBase-Forwarded-For` (the client address the edge reported). The
  home serves it as a client-listener request: the same token checks, IP allowlists, quotas and metering, once, at the home.
  `X-TerraBase-Region` names the home. A forwarded request is never forwarded again. When the home cannot be reached the
  answer is `421 wrong_region` with `X-TerraBase-Database-Home` and the home's regional host. Listing a project's
  databases is forwarded to a region that holds the project when this one does not.
- **Postgres wire.** A dashboard API key is exchanged at ts-auth in the region the session reached; when its project or
  database is homed elsewhere, the session is relayed to the home for its whole life (`POST /v1/internal/postgres`, a
  duplex HTTP/2 stream on the internal listener), and the home authenticates it again. An unreachable home answers
  `FATAL 08001` naming the home region; a database that exists in no region answers `FATAL 3D000`. A cancel request sent
  to the relaying region does not reach the home.
- **Replica regions.** A Postgres or WebSocket session that reaches a replica region of its database is not relayed: it
  reads from the replica and runs writes and transactions on a session at the home
  ([region replicas](region-replicas.md#writes-and-transactions)).
- `X-TerraBase-Forwarded-For` and the relay are honored only on the internal listener with a matching peer credential;
  public forwarded headers are never trusted.

## Region discovery

Serving regions come from `TS_REGION` and the ts-orch peers file `TS_PEERS_FILE` ([ts-orch.md](ts-orch.md)). Peers
are used for routing and private listener binding; discovering a region grants no placement and creates no copy.

## Tests

Fast covers placement and its refusals in `tests/TerraBase.Fast/Management/RegionCatalogTests.cs` and `DatabaseRegionTests.cs` (the reported region across restarts).
