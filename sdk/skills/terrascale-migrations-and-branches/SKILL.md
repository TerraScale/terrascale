---
name: terrascale-migrations-and-branches
description: Manage TerraScale schema migrations and database branches, including a database per pull request in CI. Use when adding tables or indexes, comparing schemas, testing migrations on branches, changing a live multi-region database, or planning backup and rollback.
---

# TerraScale migrations and branches

## Prerequisites

- [ ] ts CLI v0.0.4 or later, authenticated to the intended project.
- [ ] SQL read/write access, plus database management access for branches/backups.
- [ ] A repository containing the authoritative schema history.
- [ ] Capacity for source, preview and recovery databases.

Sources: [migrations](https://docs.terrascale.tech/guides/migrations),
[branches](https://docs.terrascale.tech/guides/branches),
[backups](https://docs.terrascale.tech/guides/backups-and-restore).

## 1. Inventory schema ownership and recovery

- [ ] Inspect project/database identity with ts projects and ts db info app.
- [ ] Choose one schema owner: ts migrate, an ORM migration system, or declarative
  schema files. Do not independently apply the same change through two systems.
- [ ] Read current history and inspect backups before risky DDL.

~~~sh
ts migrate status app
ts backup list app
ts backup window app
~~~

An API key in TERRASCALE_API_KEY replaces login for database commands.
Organization keys also require -project ID before the command.
Auth, whoami, projects and keys still need interactive sign-in.
Source: [migration CI](https://docs.terrascale.tech/guides/migrations).

## 2. Create an isolated branch

~~~sh
ts db branch create app-orders --from app --ttl 7d
ts db branch list
~~~

Branches contain an independent snapshot; later writes do not merge between source
and branch. Names start with a lowercase letter and use at most 63 lowercase
letters, digits, underscores or hyphens. Map feature/orders to feature-orders.
TTL defaults to seven days; accepted values range from one minute to 365 days.
Branches do not inherit parent expiry. Expiry permanently deletes the branch,
and each live branch consumes a database slot.
Source: [branches](https://docs.terrascale.tech/guides/branches).

The two-operand command ts db branch app app-orders creates a permanent copy.
Source: [CLI branches](https://docs.terrascale.tech/cli).

With a suitable API key in the environment, capture a connection privately:

~~~sh
set +x
DATABASE_URL="$(ts db branch url app-orders)"
export DATABASE_URL
~~~

The URL contains a newly issued branch-scoped read/write key and uses the branch
immutable ID. It expires within seven days or at branch expiry, whichever is sooner.
Never echo it. Issuance requires an API key able to issue scoped keys.
Source: [branch connections](https://docs.terrascale.tech/guides/branches).

## 3. Write and apply ordered migration files

~~~sh
ts migrate new "add orders"
~~~

For a fresh history this creates migrations/0001_add_orders.sql. Replace its
generated heading with the intended SQL:

~~~sql
CREATE TABLE orders (
  id TEXT PRIMARY KEY,
  customer TEXT NOT NULL,
  total_cents INTEGER NOT NULL
);
CREATE INDEX orders_customer ON orders (customer);
~~~

Files live under ./migrations relative to the working directory.
Use NNNN_name.sql: four digits, underscore, lowercase letters/digits/underscores.
A file can contain up to 255 statements, each at most 16 KiB. Do not add BEGIN or
COMMIT: each file and its history row run in one transaction.
Source: [migration files](https://docs.terrascale.tech/guides/migrations).

~~~sh
ts migrate status app-orders
ts migrate up app-orders
ts migrate diff app-orders
ts sql app-orders "SELECT id, customer, total_cents FROM orders LIMIT 10"
~~~

Up records name, SHA-256 checksum and applied_at in ts_migrations. Reapplying an
unchanged history does nothing. Never edit an applied file: add the next migration.
A failed file commits nothing, but earlier successful files remain applied.

| Status | Meaning and action |
| --- | --- |
| pending | Review and apply on the preview first. |
| applied | File matches recorded history. |
| changed | Restore the original file; put new changes in a later migration. |
| applied, file missing | Restore the missing history file before shipping. |

Source: [migrations](https://docs.terrascale.tech/guides/migrations).

Diff defaults to the branch's source; an explicit comparison is:

~~~sh
ts migrate diff app-orders app
~~~

It compares tables, columns, declared types and indexes, but not NOT NULL, defaults
or primary keys. Inspect those separately with schema-reading PRAGMAs and app
behavior; a clean diff alone is insufficient.
Source: [compare schemas](https://docs.terrascale.tech/guides/migrations).

For a declarative owner, alternatives are ts schema pull app,
ts schema diff app schema.sql and ts schema apply app schema.sql. Apply is atomic;
-dry-run previews and -allow-drop consents to DROP operations. Do not mix this
with a competing migration history.
Source: [CLI schema files](https://docs.terrascale.tech/cli).

## 4. Verify indexes and live multi-region changes

- [ ] Measure lookup plans before and after indexing.
- [ ] Keep DDL/backfill requests within transaction limits.
- [ ] Deploy compatible application reads/writes before removing old fields.
- [ ] Pause concurrent writers for a non-atomic table copy/cutover.
- [ ] Check transaction outcomes as well as conflicts after migration.

~~~sh
ts sql app-orders "EXPLAIN QUERY PLAN SELECT id FROM orders WHERE customer = 'Ada'"
ts sql app-orders "PRAGMA index_list(orders)"
~~~

Indexes support plain, UNIQUE, partial and expression forms. DDL routes to the
region ordering writes and needs sql:write. Look for SEARCH ... USING INDEX;
a leading column or type mismatch can prevent index use.
Source: [indexes](https://docs.terrascale.tech/guides/indexes).

Transactions change at most 1,024 rows and grow files by at most 8 MiB, with a
five-second transaction limit. Large table rebuilds need a separately planned
chunked copy with concurrent writers paused or tolerable.
Sources: [limits](https://docs.terrascale.tech/limits),
[batch rebuilds](https://docs.terrascale.tech/guides/orm-sqlalchemy).

Conflicting writes to one column use the greater HLC stamp, not arrival order;
different columns can both survive. Provisional means locally committed but not
yet applied at the ordering region. Confirmed does not mean every column won;
reverted means the whole transaction was rejected. An empty conflict list does
not prove confirmation. Inspect **Conflicts** in the dashboard or query:

~~~sql
SELECT id, table_name, loser_state, created_at
FROM terrascale.conflicts
WHERE table_name = 'orders'
ORDER BY id DESC
LIMIT 50;
~~~

Use parameters for application filters.
Source: [multi-region writes](https://docs.terrascale.tech/guides/multi-region-writes-and-conflicts).
REST reads can lag; use session consistency for verification. Multi-region copies
follow changes and are not backups.
Source: [replication](https://docs.terrascale.tech/concepts/replication).

## 5. Put a branch per pull request in CI

Read [the CI workflow](references/pull-request-ci.md) for the complete example,
pinned action, scoped migration credentials, cleanup and publication prerequisites.
Public guide: [branches](https://docs.terrascale.tech/guides/branches).

## 6. Back up, cut over and roll back

Before production application:

~~~sh
ts backup create app
ts backup list app
ts backup window app
~~~

Record a ready backup ID and proven restore timestamp. Preview an RFC3339 point
with ts backup plan app "$RESTORE_TIME". A plan is advisory, not proof of valid
replay bytes.
Source: [backups](https://docs.terrascale.tech/guides/backups-and-restore).

- [ ] Rehearse restore into a new database; validate rows and app access.
- [ ] Apply reviewed files once to production, then check history and queries.

~~~sh
ts migrate up app
ts migrate status app
~~~

Restore with BACKUP_ID and RESTORE_TIME supplied from observed metadata:

~~~sh
ts backup restore app "$BACKUP_ID" app-restored
ts backup restore-at app "$RESTORE_TIME" app-restored-at
~~~

Restore creates a new database without overwriting the source. Validate it before
a separate credential/routing cutover. Check that background restore completes;
accepted work is not a completed copy.
Sources: [CLI restore](https://docs.terrascale.tech/cli),
[restore operations](https://docs.terrascale.tech/guides/backups-and-restore).

Prefer a forward corrective migration when safe. For in-place rewind, obtain
explicit approval for the exact database and timestamp before running
ts -yes backup rewind app "$RESTORE_TIME". Rewind preserves identity/credentials
but discards changes committed after that instant.
Source: [rewind](https://docs.terrascale.tech/guides/backups-and-restore).

Delete previews after inspection:

~~~sh
ts db branch delete app-orders --yes
~~~

Deletion verifies the branch and conditionally deletes its observed ID;
--expected-id can require a previously inspected ULID.
Source: [branch deletion](https://docs.terrascale.tech/guides/branches).

For infrastructure ownership, see the
[Terraform provider README](https://github.com/TerraScale/terraform-provider-terrascale#readme).
It prepares an initial 0.0.1 release; Registry publication still needs setup.
Treat it as a separate infrastructure workflow, not a SQL migration runner.
