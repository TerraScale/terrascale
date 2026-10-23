---
title: SQL compatibility
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/pt-br/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, see [Postgres clients](/pt-br/guides/postgres-clients). The Documents API also accepts read-only `SELECT` queries: the [Go quickstart](/pt-br/getting-started/go) runs one and the [CLI](/pt-br/cli#run-sql) has `ts sql`. Neither runs writes, DDL or cross-database transactions.

</div>
