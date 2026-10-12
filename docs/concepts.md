---
title: TerraBase concepts
---

# TerraBase concepts

TerraBase stores your application's JSON documents in named collections within a project-scoped database. An SDK handle selects a resource; issuing an operation contacts its authorized endpoint.

Documents carry opaque versions for conditional updates. Queries use JSON filters and opaque continuation cursors. See [queries and pagination](/guides/queries).

[Replication](/concepts/replication) distinguishes current write results from multi-region product requirements. [Live updates](/concepts/sync) and [SQL compatibility](/concepts/sql) describe separate contracts and their availability boundaries.
