---
title: Practical guides
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Practical guides

Use the [quickstart](/pt-br/getting-started/) to establish one working application connection, then choose the guide for the next operation.

- [SDK reference](/pt-br/guides/sdks): public installation, language support and API compatibility.
- [Authentication capabilities](/pt-br/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/pt-br/guides/authentication): credential scope and server/browser boundaries.
- [Queries and pagination](/pt-br/guides/queries): filters, projection, cursors and version preconditions.
- [Connect with psql and Postgres drivers](/pt-br/guides/postgres-clients): SQL over the PostgreSQL wire protocol from psql and standard drivers (pending).
- [Connect with libSQL clients](/pt-br/guides/libsql-clients): SQL from the official libSQL clients (pending).
- [Use Drizzle ORM](/pt-br/guides/orm-drizzle), [Prisma ORM](/pt-br/guides/orm-prisma) and [SQLAlchemy](/pt-br/guides/orm-sqlalchemy): ORMs over the libSQL clients.
- [Errors and safe retries](/pt-br/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/pt-br/guides/indexes): secondary indexes, recommendations and automatic indexing.
- [Schema validation](/pt-br/guides/schema-validation): JSON Schema checks and unique keys per collection.
- [Generate TypeScript document models](/pt-br/guides/schema-validation#generate-typescript-document-models) (upcoming): Node tooling for declared collection schemas.
- [Schema preview and request diagnostics](/pt-br/guides/schema-preview-request-diagnostics) (pending rollout): bounded historical validation and request IDs for support.
- [Expire documents with TTL](/pt-br/guides/ttl): automatic deletion after a per-document expiry time, with about an hour of lag.
- [Vector similarity search](/pt-br/guides/vector-search): nearest-neighbour queries over stored embeddings for semantic search and RAG.
- [Transactions](/pt-br/guides/transactions): atomic multi-document writes with conditional asserts.
- [Realtime changes](/pt-br/guides/realtime): the server-sent changes feed and filtered collection shapes.
- [Functions](/pt-br/guides/functions): JavaScript functions called over HTTPS, on collection changes or on a cron schedule.
- [Metrics, log drains and alerts](/pt-br/guides/monitoring): Prometheus scraping, OTLP log drains, and alert emails and webhooks.
- [Backups and restore](/pt-br/guides/backups-and-restore): backup policy, point-in-time restore into a new database.
- [Export your data](/pt-br/guides/export): stream every document as NDJSON, resume and restore.
- [Branches](/pt-br/guides/branches): copy a database into a new branch database, and preview databases for pull requests.
- [Schema migrations](/pt-br/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Dashboard usage](/pt-br/guides/dashboard): customer account and database workflow.
- [Single sign-on](/pt-br/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import an existing database](/pt-br/guides/import) (pending): one-time import of a PostgreSQL or Turso export with dry-run preview and validation.
- [Import from MongoDB or Firestore](/pt-br/guides/import-ndjson): mongoexport or a Firestore script into one NDJSON file, then the same import.

The [zero-configuration contract](/pt-br/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.

</div>
