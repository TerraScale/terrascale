---
title: TerraBase concepts
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# TerraBase concepts

TerraBase stores your application's JSON documents in named collections within a project-scoped database. An SDK handle selects a resource; issuing an operation contacts its authorized endpoint.

Documents carry opaque versions for conditional updates. Queries use JSON filters and opaque continuation cursors. See [queries and pagination](/ru/guides/queries).

[Replication](/ru/concepts/replication) distinguishes current write results from multi-region product requirements. [Live updates](/ru/concepts/sync) and [SQL compatibility](/ru/concepts/sql) describe separate contracts and their availability boundaries.

</div>
