# Platform database placement lock

The platform databases (`ts-auth` and `ts-dashboard`, [platform-databases.md](platform-databases.md)) are seeded on
their own: `ts-dashboard` in each region, homed in the region that seeds it, and `ts-auth` only in gru, its home. Each
is homed where it is seeded, with no replicas and placement version 1. That placement cannot change.

## API

On a database of the platform organization:

- A placement change through `PATCH /v1/projects/{project}/databases/{database}` (`allRegions` or `replicaRegions`)
  answers 200 with the unchanged database. It writes nothing, so the record keeps its home, its empty replica list and
  its placement version.
- Moving its home answers `409 platform_placement_locked`. The catalog record is unchanged.

Peer placement updates skip records homed here, and home leases skip the platform databases. This placement rule does not
change deletion authorization: deleting, renaming or setting a TTL on a platform database is refused on its own
([platform-databases.md](platform-databases.md)).

## Restore and branch

- A branch copies the source placement (`EngineManagementService.BranchDatabaseAsync`), so a branch of a platform
  database is homed in the same region with no replicas. As a database of the platform organization, it keeps that
  placement as well.
- Whole-backup, point-in-time and selective restores of platform sources answer `409 recovery_erasure_fenced`
  ([copy placement](recovery-copy-placement.md)). No restore can produce a platform copy
  with another placement.

This changes no storage format or replication protocol.

## Proof

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
