---
title: Practical guides
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# Practical guides

Use the [quickstart](/ar/getting-started/) to establish one working application connection, then choose the guide for the next operation. For the SQL model, see [SQL](/ar/concepts/sql).

- [SDK reference](/ar/guides/sdks)
- [Authentication capabilities](/ar/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/ar/guides/authentication): API keys, access tokens, IP allowlists, revocation and two-factor sign-in.
- [Connect with psql and Postgres drivers](/ar/guides/postgres-clients): host, credentials, dialect and transactions for psql, node-postgres, psycopg, pgx and Npgsql.
- [SQL over HTTP](/ar/guides/sql-http): run SQL statements with any HTTP client.
- [Use Drizzle ORM](/ar/guides/orm-drizzle): run Drizzle's SQLite dialect over the JavaScript SDK.
- [Use Prisma ORM](/ar/guides/orm-prisma): run Prisma's SQLite dialect over the JavaScript SDK's driver adapter.
- [Use SQLAlchemy and Alembic](/ar/guides/orm-sqlalchemy): run SQLAlchemy's SQLite dialect, the ORM and Alembic migrations over the Python SDK.
- [Typed SQL with the Rust SDK](/ar/guides/rust-sql): queries, typed `query_as` selects, transactions over the session and batches in Rust.
- [Errors and safe retries](/ar/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/ar/guides/indexes): secondary, unique, partial and expression indexes with `CREATE INDEX`, `DROP INDEX` and `EXPLAIN QUERY PLAN`.
- [Functions](/ar/guides/functions)
- [Monitoring](/ar/guides/monitoring): usage counters, request analytics, Prometheus metrics, request logs and the status page.
- [Backups and restore](/ar/guides/backups-and-restore)
- [Rename a database](/ar/guides/rename-database): change a database's name from the dashboard, API, CLI or SDKs.
- [File storage](/ar/guides/file-storage): project files in buckets with owner, role and public read and write rules.
- [Schema migrations](/ar/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Branches](/ar/guides/branches)
- [Multi-region writes and conflicts](/ar/guides/multi-region-writes-and-conflicts): column winners, transaction states and the conflict log.
- [Dashboard usage](/ar/guides/dashboard): customer account and database workflow.
- [Connect an AI assistant with MCP](/ar/guides/mcp): sign in or use a personal access token to connect an assistant.
- [Single sign-on](/ar/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import data](/ar/guides/import): load rows into a database with batched SQL statements.

The [zero-configuration contract](/ar/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.

</div>
