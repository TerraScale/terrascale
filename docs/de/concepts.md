---
title: TerraBase concepts
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# TerraBase concepts

TerraBase stores your application's JSON documents in named collections within a project-scoped database. An SDK handle selects a resource; issuing an operation contacts its authorized endpoint.

Documents carry opaque versions for conditional updates. Queries use JSON filters and opaque continuation cursors. See [queries and pagination](/de/guides/queries).

[Replication](/de/concepts/replication) distinguishes current write results from multi-region product requirements. [Live updates](/de/concepts/sync) and [SQL compatibility](/de/concepts/sql) describe separate contracts and their availability boundaries.

</div>
