---
title: TerraBase concepts
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# TerraBase concepts

TerraBase stores your application's JSON documents in named collections within a project-scoped database. An SDK handle selects a resource; issuing an operation contacts its authorized endpoint.

Documents carry opaque versions for conditional updates. Queries use JSON filters and opaque continuation cursors. See [queries and pagination](/pt-br/guides/queries).

[Replication](/pt-br/concepts/replication) distinguishes current write results from multi-region product requirements. [Live updates](/pt-br/concepts/sync) and [SQL compatibility](/pt-br/concepts/sql) describe separate contracts and their availability boundaries.

</div>
