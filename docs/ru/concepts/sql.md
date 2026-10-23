---
title: SQL compatibility
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/ru/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, see [Postgres clients](/ru/guides/postgres-clients). The Documents API also accepts read-only `SELECT` queries: the [Go quickstart](/ru/getting-started/go) runs one and the [CLI](/ru/cli#run-sql) has `ts sql`. Neither runs writes, DDL or cross-database transactions.

</div>
