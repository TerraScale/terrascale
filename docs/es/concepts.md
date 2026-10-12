---
title: TerraBase concepts
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# TerraBase concepts

TerraBase stores your application's JSON documents in named collections within a project-scoped database. An SDK handle selects a resource; issuing an operation contacts its authorized endpoint.

Documents carry opaque versions for conditional updates. Queries use JSON filters and opaque continuation cursors. See [queries and pagination](/es/guides/queries).

[Replication](/es/concepts/replication) distinguishes current write results from multi-region product requirements. [Live updates](/es/concepts/sync) and [SQL compatibility](/es/concepts/sql) describe separate contracts and their availability boundaries.

</div>
