---
title: Practical guides
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Practical guides

Use the [quickstart](/de/getting-started/) to establish one working application connection, then choose the guide for the next operation.

- [SDK reference](/de/guides/sdks): public installation, language support and API compatibility.
- [Authentication capabilities](/de/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/de/guides/authentication): credential scope and server/browser boundaries.
- [Queries and pagination](/de/guides/queries): filters, projection, cursors and version preconditions.
- [Connect with psql and Postgres drivers](/de/guides/postgres-clients): SQL over the PostgreSQL wire protocol from psql and standard drivers (pending).
- [Connect with libSQL clients](/de/guides/libsql-clients): SQL from the official libSQL clients (pending).
- [Use Drizzle ORM](/de/guides/orm-drizzle), [Prisma ORM](/de/guides/orm-prisma) and [SQLAlchemy](/de/guides/orm-sqlalchemy): ORMs over the libSQL clients.
- [Errors and safe retries](/de/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/de/guides/indexes): secondary indexes, recommendations and automatic indexing.
- [Schema validation](/de/guides/schema-validation): JSON Schema checks and unique keys per collection.
- [Generate TypeScript document models](/de/guides/schema-validation#generate-typescript-document-models) (upcoming): Node tooling for declared collection schemas.
- [Schema preview and request diagnostics](/de/guides/schema-preview-request-diagnostics) (pending rollout): bounded historical validation and request IDs for support.
- [Expire documents with TTL](/de/guides/ttl): automatic deletion after a per-document expiry time, with about an hour of lag.
- [Vector similarity search](/de/guides/vector-search): nearest-neighbour queries over stored embeddings for semantic search and RAG.
- [Transactions](/de/guides/transactions): atomic multi-document writes with conditional asserts.
- [Realtime changes](/de/guides/realtime): the server-sent changes feed and filtered collection shapes.
- [Functions](/de/guides/functions): JavaScript functions called over HTTPS, on collection changes or on a cron schedule.
- [Metrics, log drains and alerts](/de/guides/monitoring): Prometheus scraping, OTLP log drains, and alert emails and webhooks.
- [Backups and restore](/de/guides/backups-and-restore): backup policy, point-in-time restore into a new database.
- [Export your data](/de/guides/export): stream every document as NDJSON, resume and restore.
- [Branches](/de/guides/branches): copy a database into a new branch database, and preview databases for pull requests.
- [Schema migrations](/de/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Dashboard usage](/de/guides/dashboard): customer account and database workflow.
- [Single sign-on](/de/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import an existing database](/de/guides/import) (pending): one-time import of a PostgreSQL or Turso export with dry-run preview and validation.
- [Import from MongoDB or Firestore](/de/guides/import-ndjson): mongoexport or a Firestore script into one NDJSON file, then the same import.

The [zero-configuration contract](/de/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.

</div>
