---
title: Practical guides
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# Practical guides

Use the [quickstart](/ru/getting-started/) to establish one working application connection, then choose the guide for the next operation.

- [SDK reference](/ru/guides/sdks): public installation, language support and API compatibility.
- [Authentication capabilities](/ru/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/ru/guides/authentication): credential scope and server/browser boundaries.
- [Queries and pagination](/ru/guides/queries): filters, projection, cursors and version preconditions.
- [Connect with psql and Postgres drivers](/ru/guides/postgres-clients): SQL over the PostgreSQL wire protocol from psql and standard drivers (pending).
- [Connect with libSQL clients](/ru/guides/libsql-clients): SQL from the official libSQL clients (pending).
- [Use Drizzle ORM](/ru/guides/orm-drizzle), [Prisma ORM](/ru/guides/orm-prisma) and [SQLAlchemy](/ru/guides/orm-sqlalchemy): ORMs over the libSQL clients.
- [Errors and safe retries](/ru/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/ru/guides/indexes): secondary indexes, recommendations and automatic indexing.
- [Schema validation](/ru/guides/schema-validation): JSON Schema checks and unique keys per collection.
- [Generate TypeScript document models](/ru/guides/schema-validation#generate-typescript-document-models) (upcoming): Node tooling for declared collection schemas.
- [Schema preview and request diagnostics](/ru/guides/schema-preview-request-diagnostics) (pending rollout): bounded historical validation and request IDs for support.
- [Expire documents with TTL](/ru/guides/ttl): automatic deletion after a per-document expiry time, with about an hour of lag.
- [Vector similarity search](/ru/guides/vector-search): nearest-neighbour queries over stored embeddings for semantic search and RAG.
- [Transactions](/ru/guides/transactions): atomic multi-document writes with conditional asserts.
- [Realtime changes](/ru/guides/realtime): the server-sent changes feed and filtered collection shapes.
- [Functions](/ru/guides/functions): JavaScript functions called over HTTPS, on collection changes or on a cron schedule.
- [Metrics, log drains and alerts](/ru/guides/monitoring): Prometheus scraping, OTLP log drains, and alert emails and webhooks.
- [Backups and restore](/ru/guides/backups-and-restore): backup policy, point-in-time restore into a new database.
- [Export your data](/ru/guides/export): stream every document as NDJSON, resume and restore.
- [Branches](/ru/guides/branches): copy a database into a new branch database, and preview databases for pull requests.
- [Schema migrations](/ru/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Dashboard usage](/ru/guides/dashboard): customer account and database workflow.
- [Single sign-on](/ru/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import an existing database](/ru/guides/import) (pending): one-time import of a PostgreSQL or Turso export with dry-run preview and validation.
- [Import from MongoDB or Firestore](/ru/guides/import-ndjson): mongoexport or a Firestore script into one NDJSON file, then the same import.

The [zero-configuration contract](/ru/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.

</div>
