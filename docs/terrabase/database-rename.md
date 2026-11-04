# Database rename

`PATCH /v1/projects/{project}/databases/{database}` with `{"name":"new-name"}` alone renames a database. `{database}`
is its immutable ULID or current name. This supersedes the "PATCH `name` returns 400" note of
[ULID addressing](database-ulid-addressing.md); everything else there still applies.

## Contract

- **What stays:** the ULID, data, files, placement, backups, backup policy and keys. Requests by ULID keep working
  throughout. Name-based connection strings must change.
- **Where:** only the database's home region accepts a rename. Any other region answers `421 wrong_region` with
  `X-TerraBase-Database-Home`.
- **Validation:** the name follows the database name rules; combining `name` with `homeRegion`, `replicaRegions` or
  `allRegions` is `400 invalid_request`. Renaming to the current name returns the database unchanged.
- **Duplicate:** a name another database of the project holds in this region's catalog is `409 name_taken`.
- **Response:** `200` with the database body as the home region committed it.
- **Old name:** released in the same transaction; it answers `404`, or resolves to whichever database claims it next.
  No alias is kept.

## Credentials

A key or token narrowed to one database carries its ULID (`database` claim); TerraBase refuses a token whose
`database` claim is a name, and ts-auth only creates keys narrowed by ULID. A rename therefore never moves a
credential to another database that later takes the old name.
