---
title: SQL compatibility
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/ja/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, see [Postgres clients](/ja/guides/postgres-clients). The Documents API also accepts read-only `SELECT` queries: the [Go quickstart](/ja/getting-started/go) runs one and the [CLI](/ja/cli#run-sql) has `ts sql`. Neither runs writes, DDL or cross-database transactions.

</div>
