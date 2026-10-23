---
title: SQL compatibility
---

# SQL compatibility

Use [JSON filter queries](/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, see [Postgres clients](/guides/postgres-clients). The Documents API also accepts read-only `SELECT` queries: the [Go quickstart](/getting-started/go) runs one and the [CLI](/cli#run-sql) has `ts sql`. Neither runs writes, DDL or cross-database transactions.
