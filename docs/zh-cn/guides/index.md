---
title: Practical guides
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Practical guides

Use the [quickstart](/zh-cn/getting-started/) to establish one working application connection, then choose the guide for the next operation.

- [SDK reference](/zh-cn/guides/sdks): public installation, language support and API compatibility.
- [Authentication capabilities](/zh-cn/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/zh-cn/guides/authentication): credential scope and server/browser boundaries.
- [Queries and pagination](/zh-cn/guides/queries): filters, projection, cursors and version preconditions.
- [Connect with psql and Postgres drivers](/zh-cn/guides/postgres-clients): SQL over the PostgreSQL wire protocol from psql and standard drivers (pending).
- [Connect with libSQL clients](/zh-cn/guides/libsql-clients): SQL from the official libSQL clients (pending).
- [Use Drizzle ORM](/zh-cn/guides/orm-drizzle), [Prisma ORM](/zh-cn/guides/orm-prisma) and [SQLAlchemy](/zh-cn/guides/orm-sqlalchemy): ORMs over the libSQL clients.
- [Errors and safe retries](/zh-cn/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/zh-cn/guides/indexes): secondary indexes, recommendations and automatic indexing.
- [Schema validation](/zh-cn/guides/schema-validation): JSON Schema checks and unique keys per collection.
- [Generate TypeScript document models](/zh-cn/guides/schema-validation#generate-typescript-document-models) (upcoming): Node tooling for declared collection schemas.
- [Schema preview and request diagnostics](/zh-cn/guides/schema-preview-request-diagnostics) (pending rollout): bounded historical validation and request IDs for support.
- [Expire documents with TTL](/zh-cn/guides/ttl): automatic deletion after a per-document expiry time, with about an hour of lag.
- [Vector similarity search](/zh-cn/guides/vector-search): nearest-neighbour queries over stored embeddings for semantic search and RAG.
- [Transactions](/zh-cn/guides/transactions): atomic multi-document writes with conditional asserts.
- [Realtime changes](/zh-cn/guides/realtime): the server-sent changes feed and filtered collection shapes.
- [Functions](/zh-cn/guides/functions): JavaScript functions called over HTTPS, on collection changes or on a cron schedule.
- [Metrics, log drains and alerts](/zh-cn/guides/monitoring): Prometheus scraping, OTLP log drains, and alert emails and webhooks.
- [Backups and restore](/zh-cn/guides/backups-and-restore): backup policy, point-in-time restore into a new database.
- [Export your data](/zh-cn/guides/export): stream every document as NDJSON, resume and restore.
- [Branches](/zh-cn/guides/branches): copy a database into a new branch database, and preview databases for pull requests.
- [Schema migrations](/zh-cn/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Dashboard usage](/zh-cn/guides/dashboard): customer account and database workflow.
- [Single sign-on](/zh-cn/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import an existing database](/zh-cn/guides/import) (pending): one-time import of a PostgreSQL or Turso export with dry-run preview and validation.
- [Import from MongoDB or Firestore](/zh-cn/guides/import-ndjson): mongoexport or a Firestore script into one NDJSON file, then the same import.

The [zero-configuration contract](/zh-cn/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.

</div>
