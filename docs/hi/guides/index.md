---
title: Practical guides
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Practical guides

Use the [quickstart](/hi/getting-started/) to establish one working application connection, then choose the guide for the next operation.

- [SDK reference](/hi/guides/sdks): public installation, language support and API compatibility.
- [Authentication capabilities](/hi/guides/authentication-capabilities): current sign-in methods, protocol scope and unavailable flows.
- [Authentication](/hi/guides/authentication): credential scope and server/browser boundaries.
- [Queries and pagination](/hi/guides/queries): filters, projection, cursors and version preconditions.
- [Connect with psql and Postgres drivers](/hi/guides/postgres-clients): SQL over the PostgreSQL wire protocol from psql and standard drivers (pending).
- [Connect with libSQL clients](/hi/guides/libsql-clients): SQL from the official libSQL clients (pending).
- [Use Drizzle ORM](/hi/guides/orm-drizzle), [Prisma ORM](/hi/guides/orm-prisma) and [SQLAlchemy](/hi/guides/orm-sqlalchemy): ORMs over the libSQL clients.
- [Errors and safe retries](/hi/guides/errors): typed failures and uncertain write outcomes.
- [Indexes](/hi/guides/indexes): secondary indexes, recommendations and query shapes.
- [Schema validation](/hi/guides/schema-validation): JSON Schema checks and unique keys per collection.
- [Generate TypeScript document models](/hi/guides/schema-validation#generate-typescript-document-models) (upcoming): Node tooling for declared collection schemas.
- [Schema preview and request diagnostics](/hi/guides/schema-preview-request-diagnostics) (pending rollout): bounded historical validation and request IDs for support.
- [Expire documents with TTL](/hi/guides/ttl): automatic deletion after a per-document expiry time, with about an hour of lag.
- [Vector similarity search](/hi/guides/vector-search): nearest-neighbour queries over stored embeddings for semantic search and RAG.
- [Transactions](/hi/guides/transactions): atomic multi-document writes with conditional asserts.
- [Realtime changes](/hi/guides/realtime): the server-sent changes feed and filtered collection shapes.
- [Functions](/hi/guides/functions): JavaScript functions called over HTTPS, on collection changes or on a cron schedule.
- [Metrics, log drains and alerts](/hi/guides/monitoring): Prometheus scraping, OTLP log drains, and alert emails and webhooks.
- [Backups and restore](/hi/guides/backups-and-restore): backup policy, point-in-time restore into a new database.
- [Export your data](/hi/guides/export): stream every document as NDJSON, resume and restore.
- [Branches](/hi/guides/branches): copy a database into a new branch database, and preview databases for pull requests.
- [Schema migrations](/hi/guides/migrations): versioned SQL table migrations with `ts migrate`.
- [Dashboard usage](/hi/guides/dashboard): customer account and database workflow.
- [Single sign-on](/hi/guides/single-sign-on): connect your identity provider over OIDC for a verified email domain (Team and Enterprise).
- [Import an existing database](/hi/guides/import) (pending): one-time import of a PostgreSQL or Turso export with dry-run preview and validation.
- [Import from MongoDB or Firestore](/hi/guides/import-ndjson): mongoexport or a Firestore script into one NDJSON file, then the same import.

The [zero-configuration contract](/hi/guides/zero-configuration) explains the platform's intended operating behavior without disclosing deployment internals.

</div>
