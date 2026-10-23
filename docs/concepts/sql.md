---
title: SQL compatibility
---

# SQL compatibility

Use [JSON filter queries](/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, see [Postgres clients](/guides/postgres-clients). The document quickstarts do not run SQL, DDL or cross-database transactions.
