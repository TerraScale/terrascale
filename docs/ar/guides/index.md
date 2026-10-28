---
title: Practical guides
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# Practical guides

Use the [quickstart](/ar/getting-started/) to establish one working application connection, then choose the guide for the next operation.

- [SDK reference](/ar/guides/sdks): public installation, language support and API compatibility.
- [Authentication capabilities](/ar/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/ar/guides/authentication): credential scope and server/browser boundaries.
- [Queries and pagination](/ar/guides/queries): filters, projection, cursors and version preconditions.
- [Connect with psql and Postgres drivers](/ar/guides/postgres-clients): SQL over the PostgreSQL wire protocol from psql and standard drivers (pending).
- [Connect with libSQL clients](/ar/guides/libsql-clients): SQL from the official libSQL clients (pending).
- [Use Drizzle ORM](/ar/guides/orm-drizzle), [Prisma ORM](/ar/guides/orm-prisma) and [SQLAlchemy](/ar/guides/orm-sqlalchemy): ORMs over the libSQL clients.
- [Errors and safe retries](/ar/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/ar/guides/indexes): secondary indexes, recommendations and query shapes.
- [Schema validation](/ar/guides/schema-validation): JSON Schema checks and unique keys per collection.
- [Generate TypeScript document models](/ar/guides/schema-validation#generate-typescript-document-models) (upcoming): Node tooling for declared collection schemas.
- [Schema preview and request diagnostics](/ar/guides/schema-preview-request-diagnostics) (pending rollout): bounded historical validation and request IDs for support.
- [Expire documents with TTL](/ar/guides/ttl): automatic deletion after a per-document expiry time, with about an hour of lag.
- [Vector similarity search](/ar/guides/vector-search): nearest-neighbour queries over stored embeddings for semantic search and RAG.
- [Transactions](/ar/guides/transactions): atomic multi-document writes with conditional asserts.
- [Realtime changes](/ar/guides/realtime): the server-sent changes feed and filtered collection shapes.
- [Functions](/ar/guides/functions): JavaScript functions called over HTTPS, on collection changes or on a cron schedule.
- [Metrics, log drains and alerts](/ar/guides/monitoring): Prometheus scraping, OTLP log drains, and alert emails and webhooks.
- [Backups and restore](/ar/guides/backups-and-restore): backup policy, point-in-time restore into a new database.
- [Export your data](/ar/guides/export): stream every document as NDJSON, resume and restore.
- [Branches](/ar/guides/branches): copy a database into a new branch database, and preview databases for pull requests.
- [Schema migrations](/ar/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Dashboard usage](/ar/guides/dashboard): customer account and database workflow.
- [Single sign-on](/ar/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import an existing database](/ar/guides/import) (pending): one-time import of a PostgreSQL or Turso export with dry-run preview and validation.
- [Import from MongoDB or Firestore](/ar/guides/import-ndjson): mongoexport or a Firestore script into one NDJSON file, then the same import.

The [zero-configuration contract](/ar/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.

</div>
