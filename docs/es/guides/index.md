---
title: Practical guides
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Practical guides

Use the [quickstart](/es/getting-started/) to establish one working application connection, then choose the guide for the next operation.

- [SDK reference](/es/guides/sdks): public installation, language support and API compatibility.
- [Authentication capabilities](/es/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/es/guides/authentication): credential scope and server/browser boundaries.
- [Queries and pagination](/es/guides/queries): filters, projection, cursors and version preconditions.
- [Connect with psql and Postgres drivers](/es/guides/postgres-clients): SQL over the PostgreSQL wire protocol from psql and standard drivers (pending).
- [Connect with libSQL clients](/es/guides/libsql-clients): SQL from the official libSQL clients (pending).
- [Use Drizzle ORM](/es/guides/orm-drizzle), [Prisma ORM](/es/guides/orm-prisma) and [SQLAlchemy](/es/guides/orm-sqlalchemy): ORMs over the libSQL clients.
- [Errors and safe retries](/es/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/es/guides/indexes): secondary indexes, recommendations and query shapes.
- [Schema validation](/es/guides/schema-validation): JSON Schema checks and unique keys per collection.
- [Generate TypeScript document models](/es/guides/schema-validation#generate-typescript-document-models) (upcoming): Node tooling for declared collection schemas.
- [Schema preview and request diagnostics](/es/guides/schema-preview-request-diagnostics) (pending rollout): bounded historical validation and request IDs for support.
- [Expire documents with TTL](/es/guides/ttl): automatic deletion after a per-document expiry time, with about an hour of lag.
- [Vector similarity search](/es/guides/vector-search): nearest-neighbour queries over stored embeddings for semantic search and RAG.
- [Transactions](/es/guides/transactions): atomic multi-document writes with conditional asserts.
- [Realtime changes](/es/guides/realtime): the server-sent changes feed and filtered collection shapes.
- [Functions](/es/guides/functions): JavaScript functions called over HTTPS, on collection changes or on a cron schedule.
- [Metrics, log drains and alerts](/es/guides/monitoring): Prometheus scraping, OTLP log drains, and alert emails and webhooks.
- [Backups and restore](/es/guides/backups-and-restore): backup policy, point-in-time restore into a new database.
- [Export your data](/es/guides/export): stream every document as NDJSON, resume and restore.
- [Branches](/es/guides/branches): copy a database into a new branch database, and preview databases for pull requests.
- [Schema migrations](/es/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Dashboard usage](/es/guides/dashboard): customer account and database workflow.
- [Single sign-on](/es/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import an existing database](/es/guides/import) (pending): one-time import of a PostgreSQL or Turso export with dry-run preview and validation.
- [Import from MongoDB or Firestore](/es/guides/import-ndjson): mongoexport or a Firestore script into one NDJSON file, then the same import.

The [zero-configuration contract](/es/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.

</div>
