---
title: TerraBase concepts
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# TerraBase concepts

TerraBase stores your application's JSON documents in named collections within a project-scoped database. An SDK handle selects a resource; issuing an operation contacts its authorized endpoint.

Documents carry opaque versions for conditional updates. Queries use JSON filters and opaque continuation cursors. See [queries and pagination](/zh-cn/guides/queries).

[Replication](/zh-cn/concepts/replication) distinguishes current write results from multi-region product requirements. [Live updates](/zh-cn/concepts/sync) and [SQL compatibility](/zh-cn/concepts/sql) describe separate contracts and their availability boundaries.

</div>
