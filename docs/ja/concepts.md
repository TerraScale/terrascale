---
title: TerraBase concepts
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# TerraBase concepts

TerraBase stores your application's JSON documents in named collections within a project-scoped database. An SDK handle selects a resource; issuing an operation contacts its authorized endpoint.

Documents carry opaque versions for conditional updates. Queries use JSON filters and opaque continuation cursors. See [queries and pagination](/ja/guides/queries).

[Replication](/ja/concepts/replication) distinguishes current write results from multi-region product requirements. [Live updates](/ja/concepts/sync) and [SQL compatibility](/ja/concepts/sql) describe separate contracts and their availability boundaries.

</div>
