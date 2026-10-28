# Database rename

`PATCH /v1/projects/{project}/databases/{database}` with `{"name":"new-name"}` alone renames a database. `{database}`
is its immutable ULID or current name. This supersedes the "PATCH `name` returns 400" note of
[ULID addressing](database-ulid-addressing.md); everything else there still applies.

## Contract

Names are unique per project after convergence; concurrent cross-region claims resolve to one winner.

- **What stays:** the ULID, data, files, placement, backups, backup policy and keys. Requests by ULID keep working
  throughout. Name-based connection strings must change.
- **Where:** only the database's home region accepts a rename. Any other region answers `421 wrong_region` with
  `X-TerraBase-Database-Home`. The home region is the one writer of a database's label, so renames of one database
  are serialized.
- **Validation:** the name follows the database name rules; combining `name` with `homeRegion`, `replicaRegions` or
  `allRegions` is `400 invalid_request`. Renaming to the current name returns the database unchanged, unless it carries
  a conflict banner, which the rename clears.
- **Same-region duplicate:** when the accepting region knows another live database of the project under the name, or
  an index holder whose record has not replicated yet, the rename is a synchronous `409 name_taken`.
- **Response:** `200` with the database body as the home region committed it. It is not a promise that every region
  already resolves the new name, or has stopped resolving the old one.
- **Old name:** the home region releases its index entry in the same transaction; after convergence the old name
  answers `404` everywhere, or resolves to whichever database claims it next. No alias is kept.
- **Audit:** `database.renamed` (actor: the caller, target: the ULID) in the organization's audit log.
- **Conflict banner:** a successful rename clears `previousName` and `conflictOf` from an earlier name conflict, and
  releases the conflict names that still pointed at the database.

## Concurrent claims across regions

Name mutations are leaderless: there is no reservation, consensus round or lock across regions. Two regions can each
accept the same name in one project at once (rename and create, or rename and rename), and both answer `2xx`.
Convergence then applies the [database name conflict](database-name-conflicts.md) rule:

- the database with the smallest ULID keeps the contested name in every region;
- the other keeps its ULID, data, files, backups and keys under the first free deterministic conflict name
  `<name>-conflict-<tail>`, with `previousName` (the contested name) and `conflictOf` (the winner's ULID) on its body,
  and one `database.name_conflict` audit event;
- its name before the losing rename is not restored: that name was released when the rename committed.

The customer renames the loser to choose another name.

Deleting a database is the only way a database leaves the catalog. A delete records purge authority at the home
region; a name conflict never does. A rename racing a delete of the same database is serialized by its home region:
the later operation sees the earlier one (a rename after the delete answers `404`).

## Stale index entries

Convergence drops index entries whose database now carries another name (from the database's home region only) or is
deleted, one bounded page of one project per pass, rotating across the projects a region holds. An entry whose
database has not replicated to the region yet is kept, so lookups fail closed.

## Credentials

A key or token narrowed to one database carries its ULID (`database` claim); TerraBase refuses a token whose
`database` claim is a name, and ts-auth only creates keys narrowed by ULID. A rename therefore never moves a
credential to another database that later takes the old name.

## Known limits

- **Late release over a new claim.** Index releases are last-writer-wins tombstones, not conditional on the claim they
  read. A release committed after, but before receiving, another region's newer claim of the same name can hide that
  claim. The claiming database's holders re-add its entry on their next convergence pass, so the name resolves again
  after one pass and replication; until then it answers `404` by name (never another database).
- **Placement full-row writes.** `ITenantDirectory.UpdatePlacementAsync`, which has no production caller today, rewrites
  the whole record from a non-home region and could restore a stale name. It must become a placement-only patch
  before anything calls it.
