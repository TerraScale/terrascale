---
title: SQL compatibility
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/ar/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, see [Postgres clients](/ar/guides/postgres-clients). The Documents API also accepts read-only `SELECT` queries: the [Go quickstart](/ar/getting-started/go) runs one and the [CLI](/ar/cli#run-sql) has `ts sql`. Neither runs writes, DDL or cross-database transactions.

</div>
