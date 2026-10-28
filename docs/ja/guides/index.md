---
title: Practical guides
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# Practical guides

Use the [quickstart](/ja/getting-started/) to establish one working application connection, then choose the guide for the next operation.

- [SDK reference](/ja/guides/sdks): public installation, language support and API compatibility.
- [Authentication capabilities](/ja/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/ja/guides/authentication): credential scope and server/browser boundaries.
- [Queries and pagination](/ja/guides/queries): filters, projection, cursors and version preconditions.
- [Connect with psql and Postgres drivers](/ja/guides/postgres-clients): SQL over the PostgreSQL wire protocol from psql and standard drivers (pending).
- [Connect with libSQL clients](/ja/guides/libsql-clients): SQL from the official libSQL clients (pending).
- [Use Drizzle ORM](/ja/guides/orm-drizzle), [Prisma ORM](/ja/guides/orm-prisma) and [SQLAlchemy](/ja/guides/orm-sqlalchemy): ORMs over the libSQL clients.
- [Errors and safe retries](/ja/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/ja/guides/indexes): secondary indexes, recommendations and query shapes.
- [Schema validation](/ja/guides/schema-validation): JSON Schema checks and unique keys per collection.
- [Generate TypeScript document models](/ja/guides/schema-validation#generate-typescript-document-models) (upcoming): Node tooling for declared collection schemas.
- [Schema preview and request diagnostics](/ja/guides/schema-preview-request-diagnostics) (pending rollout): bounded historical validation and request IDs for support.
- [Expire documents with TTL](/ja/guides/ttl): automatic deletion after a per-document expiry time, with about an hour of lag.
- [Vector similarity search](/ja/guides/vector-search): nearest-neighbour queries over stored embeddings for semantic search and RAG.
- [Transactions](/ja/guides/transactions): atomic multi-document writes with conditional asserts.
- [Realtime changes](/ja/guides/realtime): the server-sent changes feed and filtered collection shapes.
- [Functions](/ja/guides/functions): JavaScript functions called over HTTPS, on collection changes or on a cron schedule.
- [Metrics, log drains and alerts](/ja/guides/monitoring): Prometheus scraping, OTLP log drains, and alert emails and webhooks.
- [Backups and restore](/ja/guides/backups-and-restore): backup policy, point-in-time restore into a new database.
- [Export your data](/ja/guides/export): stream every document as NDJSON, resume and restore.
- [Branches](/ja/guides/branches): copy a database into a new branch database, and preview databases for pull requests.
- [Schema migrations](/ja/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Dashboard usage](/ja/guides/dashboard): customer account and database workflow.
- [Single sign-on](/ja/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import an existing database](/ja/guides/import) (pending): one-time import of a PostgreSQL or Turso export with dry-run preview and validation.
- [Import from MongoDB or Firestore](/ja/guides/import-ndjson): mongoexport or a Firestore script into one NDJSON file, then the same import.

The [zero-configuration contract](/ja/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.

</div>
