---
title: Practical guides
---

# Practical guides

Use the [quickstart](/getting-started/) to establish one working application connection, then choose the guide for the next operation.

- [SDK reference](/guides/sdks): public installation, language support and API compatibility.
- [Authentication capabilities](/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/guides/authentication): credential scope and server/browser boundaries.
- [Queries and pagination](/guides/queries): filters, projection, cursors and version preconditions.
- [Connect with psql and Postgres drivers](/guides/postgres-clients): SQL over the PostgreSQL wire protocol from psql and standard drivers (pending).
- [Connect with libSQL clients](/guides/libsql-clients): SQL from the official libSQL clients (pending).
- [Use Drizzle ORM](/guides/orm-drizzle), [Prisma ORM](/guides/orm-prisma) and [SQLAlchemy](/guides/orm-sqlalchemy): ORMs over the libSQL clients.
- [Errors and safe retries](/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/guides/indexes): secondary indexes, recommendations and query shapes.
- [Schema validation](/guides/schema-validation): JSON Schema checks and unique keys per collection.
- [Generate TypeScript document models](/guides/schema-validation#generate-typescript-document-models) (upcoming): Node tooling for declared collection schemas.
- [Schema preview and request diagnostics](/guides/schema-preview-request-diagnostics) (pending rollout): bounded historical validation and request IDs for support.
- [Expire documents with TTL](/guides/ttl): automatic deletion after a per-document expiry time, with about an hour of lag.
- [Vector similarity search](/guides/vector-search): nearest-neighbour queries over stored embeddings for semantic search and RAG.
- [Transactions](/guides/transactions): atomic multi-document writes with conditional asserts.
- [Realtime changes](/guides/realtime): the server-sent changes feed and filtered collection shapes.
- [Functions](/guides/functions): JavaScript functions called over HTTPS, on collection changes or on a cron schedule.
- [Metrics, log drains and alerts](/guides/monitoring): Prometheus scraping, OTLP log drains, and alert emails and webhooks.
- [Backups and restore](/guides/backups-and-restore): backup policy, point-in-time restore into a new database.
- [Export your data](/guides/export): stream every document as NDJSON, resume and restore.
- [Branches](/guides/branches): copy a database into a new branch database, and preview databases for pull requests.
- [Schema migrations](/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Dashboard usage](/guides/dashboard): customer account and database workflow.
- [Single sign-on](/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import an existing database](/guides/import) (pending): one-time import of a PostgreSQL or Turso export with dry-run preview and validation.
- [Import from MongoDB or Firestore](/guides/import-ndjson): mongoexport or a Firestore script into one NDJSON file, then the same import.

The [zero-configuration contract](/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.
