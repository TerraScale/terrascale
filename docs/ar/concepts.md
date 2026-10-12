---
title: TerraBase concepts
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# TerraBase concepts

TerraBase stores your application's JSON documents in named collections within a project-scoped database. An SDK handle selects a resource; issuing an operation contacts its authorized endpoint.

Documents carry opaque versions for conditional updates. Queries use JSON filters and opaque continuation cursors. See [queries and pagination](/ar/guides/queries).

[Replication](/ar/concepts/replication) distinguishes current write results from multi-region product requirements. [Live updates](/ar/concepts/sync) and [SQL compatibility](/ar/concepts/sql) describe separate contracts and their availability boundaries.

</div>
