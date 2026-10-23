---
title: SQL compatibility
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/fr/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, use [SQL over HTTP](/fr/guides/sql-http), the [libSQL clients](/fr/guides/libsql-clients) or [Postgres clients](/fr/guides/postgres-clients). The Documents API also accepts read-only `SELECT` queries: the [Go quickstart](/fr/getting-started/go) runs one and the [CLI](/fr/cli#run-sql) has `ts sql`. Neither runs writes, DDL or cross-database transactions.

</div>
