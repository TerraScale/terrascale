---
title: TerraBase concepts
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# TerraBase concepts

TerraBase stores your application's JSON documents in named collections within a project-scoped database. An SDK handle selects a resource; issuing an operation contacts its authorized endpoint.

Documents carry opaque versions for conditional updates. Queries use JSON filters and opaque continuation cursors. See [queries and pagination](/fr/guides/queries).

[Replication](/fr/concepts/replication) distinguishes current write results from multi-region product requirements. [Live updates](/fr/concepts/sync) and [SQL compatibility](/fr/concepts/sql) describe separate contracts and their availability boundaries.

</div>
