---
name: terrascale-orm-setup
description: Configure TerraScale ORM and typed SQL integrations with explicit schema ownership. Use when setting up Drizzle, Prisma, SQLAlchemy and Alembic, Go GORM/sqlc/bun, .NET Dapper or ADO.NET, or Rust typed SQL, and checking supported drivers and dialect limits.
---

# Set up an ORM on TerraScale

## Prerequisites

- [ ] Existing project, database and database-scoped SQL read/write key.
- [ ] TERRASCALE_PROJECT_ID, TERRASCALE_DATABASE_NAME and TERRASCALE_API_KEY
  supplied through the application environment or secret manager.
- [ ] The language toolchain and any source checkout the selected guide requires.

Do not print keys, tokens or secret connection strings. See also: terrascale-sdks
for general SDK usage.
Source: [getting started](https://docs.terrascale.tech/getting-started/).

## 1. Inventory the application's needs

Choose an ORM for model mapping, query construction or an existing framework.
Prefer direct parameterized SDK SQL when that meets the need or the requested ORM
is unsupported. Do not select a Postgres ORM just because a wire connection works:
TerraScale uses the **SQLite dialect**, and Postgres catalog introspection required
by some ORMs is unsupported.
Sources: [SQL](https://docs.terrascale.tech/concepts/sql),
[Postgres clients](https://docs.terrascale.tech/guides/postgres-clients).

## 2. Select and read one setup reference

| Stack | Read | Migration owner |
| --- | --- | --- |
| Drizzle sqlite-proxy | [Drizzle](references/drizzle.md) | Generate SQL; ts migrate applies it. |
| Prisma SQLite adapter | [Prisma](references/prisma.md) | Generate SQL; ts migrate applies it. |
| SQLAlchemy dialect | [SQLAlchemy/Alembic](references/sqlalchemy.md) | Alembic, or a separately chosen ts history. |
| Go database/sql | [GORM, sqlc, bun](references/go.md) | Choose runtime schema helpers or ts history. |
| .NET | [Dapper/ADO.NET](references/dotnet.md) | SQL migrations; EF Core is unsupported. |
| Rust | [Typed SQL](references/rust.md) | SQL migrations; native SDK, not a published ORM adapter. |

Each reference cites its public guide and distinguishes released packages from
source-only integrations. Do not substitute a registry package when it lacks
the documented adapter.

## 3. Establish one schema owner

- [ ] Record which system generates SQL and which applies it.
- [ ] Keep model definitions and actual database schema in agreement.
- [ ] Use a preview branch to apply and inspect the generated SQL.
- [ ] Do not run create_all, AutoMigrate or equivalent runtime schema creation
  alongside an independently managed production migration history.

For ts ownership, create migrations/NNNN_name.sql with ts migrate new,
put reviewed SQL in it, then run ts migrate up <preview>. Keep applied files
immutable; checksums are recorded in ts_migrations. ORM generators may emit
different layouts: copy reviewed SQL into the numbered ts file rather than
assuming their folders are discovered.
Source: [migrations](https://docs.terrascale.tech/guides/migrations).

## 4. Verify queries, transactions and limits

- [ ] Create the schema through its chosen owner, then insert and read a bound value.
- [ ] Verify an invalid write rolls back the intended transaction boundary.
- [ ] Check generated SQL against supported features, not just its syntax.
- [ ] Keep transactions short; avoid unrelated network work inside callbacks.

HTTP requests are atomic batches of 1–256 statements. Interactive transactions
last at most five seconds. Per transaction: at most 1,024 changed rows and 8 MiB
file growth. Plan timeouts and scan budgets can be stricter.
Sources: [SQL](https://docs.terrascale.tech/concepts/sql),
[limits](https://docs.terrascale.tech/limits).

## 5. Deploy and recover

- [ ] Back up and rehearse changes on an independent branch before production.
- [ ] Apply migrations once through the chosen owner, then deploy matching models.
- [ ] For failure, stop the rollout and inspect applied history before replay.
- [ ] Prefer a corrective migration; validate a restored copy before routing to it.
  In-place rewind discards later writes and needs explicit consent.

Sources: [branches](https://docs.terrascale.tech/guides/branches),
[backups and restore](https://docs.terrascale.tech/guides/backups-and-restore).
