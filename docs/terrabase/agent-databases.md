# Databases for agents

Every agent, session or test run gets its own database: create it, fork it, let it expire, and hand the agent a key that
reaches only that database. SDK names: `sdk/SQL-API.md`, section "Databases for agents".

## Create with a TTL

`POST /v1/projects/{project}/databases` `{"name":"run-42","ttlSeconds":3600}` (scope `databases:write`) answers 201 with
the database and `expiresAt`. `ttlSeconds` is 60 to 31,536,000; without it `expiresAt` is null and the database stays
until deleted.

## Fork

- Now: `POST …/databases/{source}/branches` `{"name":"run-42","ttlSeconds":3600}`: a consistent copy of the source
  taken now.
- At a time: `POST …/databases/{source}/point-in-time/restore` `{"name":"run-42","pointInTime":"…","ttlSeconds":3600}`:
  every transaction committed before `pointInTime`, from continuous backup ([backup.md](backup.md)).

A fork is a new database with `branchedFrom` set; it does not inherit the source's TTL. Both run in the source's home
region (421 `wrong_region` elsewhere).

## TTL

- `PUT …/databases/{database}/ttl` `{"ttlSeconds":600}` sets `expiresAt` to now plus `ttlSeconds`.
- `DELETE …/databases/{database}/ttl` clears it.

Storage: one document per database in the system catalog collection `database_expiry`, written in the same
transaction as the catalog row and removed with it; no catalog format change. The home region's catalog convergence
pass (every 2 seconds) checks for due databases at most every 5 seconds, up to 64 per pass, and deletes each through the
customer delete path: the catalog row is retired, local files are cleaned up, backups are swept and the database's
backup data keys are destroyed ([crypto-shredding.md](crypto-shredding.md)). A database is deleted within seconds of
`expiresAt`, never before. Until then it counts against the plan's database limit and is billed like any other.

## Scoped keys

`POST https://auth.terrascale.tech/v1/terrascale/api-keys/self/scoped-keys` with `Authorization: Basic
base64(keyId:key)` of an API key and `{"project","database","access","ttl_seconds"}` answers 201
`{"key","id","project_id","database","access","expires_at"}`.

- `database` is the ULID; `access` is `read` (`sql:read`) or `read_write` (`sql:read sql:write`).
- `ttl_seconds` 60 to 2,592,000, default 3600; never past the parent's expiry. `project` is required for an
  organization key and must equal a project key's project.
- The parent must hold the scopes and, if narrowed, the same database; otherwise 403. A scoped key cannot mint keys,
  manage databases (no `databases:*` scope) or reach another database: TerraBase answers 404 for a token whose `database`
  claim names another database.
- The scoped key stores its parent's id and the `key_gen` of the parent string that minted it. Every token exchange and
  every TerraBase revocation check (`GET /v1/terrascale/api-keys/self`) also requires the parent to act with that key
  string, so revoking or rotating the parent (after any rotation grace) revokes its scoped keys within the usual
  revocation bounds ([key-revocation.md](key-revocation.md)). The scoped key inherits the parent's IP allowlist.
- 600 scoped keys per parent key per minute.
