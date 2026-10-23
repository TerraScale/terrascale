---
title: SQL compatibility
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/hi/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, use [SQL over HTTP](/hi/guides/sql-http), the [libSQL clients](/hi/guides/libsql-clients) or [Postgres clients](/hi/guides/postgres-clients). The Documents API also accepts read-only `SELECT` queries: the [Go quickstart](/hi/getting-started/go) runs one and the [CLI](/hi/cli#run-sql) has `ts sql`. Neither runs writes, DDL or cross-database transactions.

</div>
