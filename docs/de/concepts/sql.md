---
title: SQL compatibility
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/de/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. It is not used with the Documents API.

For SQL, see [Postgres clients](/de/guides/postgres-clients). The Documents API also accepts read-only `SELECT` queries: the [Go quickstart](/de/getting-started/go) runs one and the [CLI](/de/cli#run-sql) has `ts sql`. Neither runs writes, DDL or cross-database transactions.

</div>
