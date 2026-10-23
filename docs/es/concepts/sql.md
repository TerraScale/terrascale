---
title: SQL compatibility
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/es/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, see [Postgres clients](/es/guides/postgres-clients). The Documents API also accepts read-only `SELECT` queries: the [Go quickstart](/es/getting-started/go) runs one and the [CLI](/es/cli#run-sql) has `ts sql`. Neither runs writes, DDL or cross-database transactions.

</div>
