# ts-auth storage on SQL tables (pivot P1)

ts-auth keeps all of its state in its TerraBase database as SQL tables. It reaches them only through the .NET SDK's SQL route
(`TerraBaseDatabase.ExecuteSqlBatchAsync`, `POST /v1/projects/{project}/databases/{database}/sql/execute`). It makes no
Documents API calls.

## Schema

At startup, `AuthStore.ProvisionAsync` runs one batch of `CREATE TABLE IF NOT EXISTS` statements, one table per entity type
(`AuthStore.RequiredCollections`, 39 tables):

```sql
CREATE TABLE "<entity>" (
  id          TEXT PRIMARY KEY NOT NULL,
  instance_id TEXT,
  version     TEXT NOT NULL CHECK (length(version) = 26),
  body        TEXT NOT NULL CHECK (json_valid(body))
);
CREATE INDEX "<entity>_instance" ON "<entity>" (instance_id, id);
CREATE TABLE store_checks (ok INTEGER NOT NULL CHECK (ok = 1));
```

- `id` is the record's ULID key. For entity records it is the natural-key ULID `AuthStore.NaturalKeyId(instance, entity, key)`:
  the first 16 bytes of a SHA-256 over the framed instance, entity and key. For closure marks it is the closure generation
  ULID. No table uses AUTOINCREMENT.
- `instance_id` is the tenant (`terrascale` or a project id). Every listing filters on it.
- `version` is a fresh ULID on every write. It is the opaque version that conditional writes name.
- `body` is the record as JSON. Filters and sorts read fields with `json_extract`.
- `store_checks` stays empty. Guards insert into it only to fail its CHECK constraint.

## Writes

Each write is one SQL request. TerraBase runs the request as one transaction and serializes writers per database, so every
request is all or nothing. A request is built in two parts:

1. **Guards**, which see the state before any write in the request. Each guard is
   `INSERT INTO store_checks (ok) SELECT 0 WHERE NOT (<condition>)`. When the condition is false, the CHECK constraint fails,
   the whole request rolls back, and `SQLITE_CONSTRAINT_CHECK` names the guard's statement index.
   - replace or delete at a version: `EXISTS (... WHERE id = ? AND version = ?)`
   - absent: `NOT EXISTS (... WHERE id = ?)`
   - assertion: `EXISTS (... WHERE id = ? AND <filter>)`
2. **Writes**: `INSERT` (create only; a taken key fails with `SQLITE_CONSTRAINT_PRIMARYKEY`), `UPDATE` or `DELETE` by id.

The statement that fails maps back to the caller's operation index. The failure is a `StorePreconditionFailedException`, or
the `int?` index from `AuthStore.TransactAsync`. Any other SQL error propagates. A missing table fails closed.

### Unique keys

The `uniques` table holds scoped natural keys: email owners, credential slots and claims. Its primary key is
`NaturalKeyId(instance, kind, key)`, so two claims on one key cannot both commit. A claim is created in the same request as
the record that owns it. It is released only by an owner-checked delete at the version that was read.

### Credential fences

Single-use challenges, refresh rotation and step-up proofs claim a `credential:*` slot in `uniques` in the same request that
consumes them. Every delegated credential keeps its ancestors' bindings. A write that depends on them adds one assertion per
binding (`AuthStore.CredentialRequirements`): the user's `authStamp`, the authorization's `status` and each claim's `ownerId`.

### Deletion and closure fences

Every write of a record in a deletion scope adds a guard that the scope's `deletion_jobs` row is absent. A job written once
blocks later writes for that subject until the job retires. Writes that add access in a pending closure's scope also guard
that the `closure_jobs` row is absent. Writes marked `AddsNoAccess` skip that guard, and so do deletes.

Account-closure cancellation and commit are local SQL transactions on `closure_cancellations` and `closure_commits`, both
keyed by generation:

- **Commit**: in one request, update the job at its version, create the commit mark, and guard that no cancellation exists.
- **Cancel**: in one request, guard that no commit mark exists, create the cancellation mark, and delete the job at its version.

Either the commit or the cancellation wins, never both. While a cancellation mark exists, a guard refuses writes of that
generation's uncommitted intent.

## Reads and pages

`GetAsync` reads one row by id. Listings filter on `instance_id` plus a JSON filter:

- `eq`, `ne`, `lt`, `lte`, `gt`, `gte`, `in`, `nin`, `and`, `or`, `not`
- `ne` matches missing fields
- booleans compare with `json_type`

Pages are keyset pages ordered by at most one field, then by `id`. A cursor is a base64url
`{"d":digest,"v":sortValue,"i":id}`. It is valid only for the same table, filter and sort; any other cursor throws
`StoreCursorException`, and the API answers 400.

## Deletion history

Deleted rows are removed from the tables in place. Their earlier after-images in TerraBase's change capture (`turso_cdc`) are
deleted by TerraBase's change tap once it consumes them (`TursoChangeTap.Truncate`). Nothing remains in a document change
log, so ts-auth no longer asks TerraBase for shared-history erasure (decision Q-P1HIST, 2026-11-03). When logical deletion is verified, a deletion job records `historyStatus: "complete"`.
It retires once `FenceExpiresAt` has passed and, for platform jobs, the dashboard has acknowledged. Backup and point-in-time
recovery retention of SQL databases belongs to TerraBase's backups (pivot E8).

## Readiness

`TerraBaseStorageHealth` reads `sqlite_schema`, requires every table, then creates, reads and deletes one synthetic
`storage_health` row at its version. Readiness never creates tables.

## Reset

This is a new schema with no migration. Production uses a fresh ts-auth database: reset it, then run the startup provisioning
and the platform bootstrap again.
