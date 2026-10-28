---
title: Practical guides
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Practical guides

Use the [quickstart](/fr/getting-started/) to establish one working application connection, then choose the guide for the next operation.

- [SDK reference](/fr/guides/sdks): public installation, language support and API compatibility.
- [Authentication capabilities](/fr/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/fr/guides/authentication): credential scope and server/browser boundaries.
- [Queries and pagination](/fr/guides/queries): filters, projection, cursors and version preconditions.
- [Connect with psql and Postgres drivers](/fr/guides/postgres-clients): SQL over the PostgreSQL wire protocol from psql and standard drivers (pending).
- [Connect with libSQL clients](/fr/guides/libsql-clients): SQL from the official libSQL clients (pending).
- [Use Drizzle ORM](/fr/guides/orm-drizzle), [Prisma ORM](/fr/guides/orm-prisma) and [SQLAlchemy](/fr/guides/orm-sqlalchemy): ORMs over the libSQL clients.
- [Errors and safe retries](/fr/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/fr/guides/indexes): secondary indexes, recommendations and query shapes.
- [Schema validation](/fr/guides/schema-validation): JSON Schema checks and unique keys per collection.
- [Generate TypeScript document models](/fr/guides/schema-validation#generate-typescript-document-models) (upcoming): Node tooling for declared collection schemas.
- [Schema preview and request diagnostics](/fr/guides/schema-preview-request-diagnostics) (pending rollout): bounded historical validation and request IDs for support.
- [Expire documents with TTL](/fr/guides/ttl): automatic deletion after a per-document expiry time, with about an hour of lag.
- [Vector similarity search](/fr/guides/vector-search): nearest-neighbour queries over stored embeddings for semantic search and RAG.
- [Transactions](/fr/guides/transactions): atomic multi-document writes with conditional asserts.
- [Realtime changes](/fr/guides/realtime): the server-sent changes feed and filtered collection shapes.
- [Functions](/fr/guides/functions): JavaScript functions called over HTTPS, on collection changes or on a cron schedule.
- [Metrics, log drains and alerts](/fr/guides/monitoring): Prometheus scraping, OTLP log drains, and alert emails and webhooks.
- [Backups and restore](/fr/guides/backups-and-restore): backup policy, point-in-time restore into a new database.
- [Export your data](/fr/guides/export): stream every document as NDJSON, resume and restore.
- [Branches](/fr/guides/branches): copy a database into a new branch database, and preview databases for pull requests.
- [Schema migrations](/fr/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Dashboard usage](/fr/guides/dashboard): customer account and database workflow.
- [Single sign-on](/fr/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import an existing database](/fr/guides/import) (pending): one-time import of a PostgreSQL or Turso export with dry-run preview and validation.
- [Import from MongoDB or Firestore](/fr/guides/import-ndjson): mongoexport or a Firestore script into one NDJSON file, then the same import.

The [zero-configuration contract](/fr/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.

</div>
