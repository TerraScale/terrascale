---
title: SQL compatibility
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/zh-cn/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, use [SQL over HTTP](/zh-cn/guides/sql-http), the [libSQL clients](/zh-cn/guides/libsql-clients) or [Postgres clients](/zh-cn/guides/postgres-clients). The Documents API also accepts read-only `SELECT` queries: the [Go quickstart](/zh-cn/getting-started/go) runs one and the [CLI](/zh-cn/cli#run-sql) has `ts sql`. Neither runs writes, DDL or cross-database transactions.

</div>
