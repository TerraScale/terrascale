# Platform database placement lock

The platform databases (`ts-auth` and `ts-dashboard`, [platform-databases.md](platform-databases.md)) are seeded in
[all-regions placement](all-regions-placement.md) with no recorded home. That placement cannot be turned off.

## API

`PATCH /v1/projects/{project}/databases/{database}` on a database of the platform organization:

- `{"allRegions": true}` alone answers 200 with the unchanged database. It writes nothing, so the record keeps no home
  and the same placement version.
- Any other body answers `409 platform_placement_locked`: `{"allRegions": false}`, any `replicaRegions` list (including
  `[]`), or `allRegions` together with `replicaRegions`. The catalog record is unchanged.

The management service enforces the same rule, so a commit `set_database_regions` operation on a platform database fails
with the same code. Since a database in every region must drop to `replicaRegions: []` before a delete
([all-regions-placement.md](all-regions-placement.md)), platform databases cannot be deleted through the API either.

## Restore and branch

- A branch copies the source placement (`EngineManagementService.BranchDatabaseAsync`), and a database of the platform
  organization is recorded with no home, so a branch of a platform database is also in every region. The lock applies
  to the branch as well.
- Whole-backup, point-in-time and selective restores of platform sources answer `409 recovery_erasure_fenced`
  ([platform-restore-erasure-fence.md](platform-restore-erasure-fence.md)). No restore can produce a platform copy
  with another placement.

This changes no storage format or replication protocol. It adds the 409 response.

## Proof

`tests/TerraBase.Fast/Platform/PlatformPlacementLockTests.cs` covers both platform databases through the operator
client. It sends each refused body and checks the code and the unchanged catalog record. It also checks that the
`allRegions: true` PATCH writes nothing and that a branch is in every region with no home and stays locked.
