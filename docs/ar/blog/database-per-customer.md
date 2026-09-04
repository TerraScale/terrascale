---
---
<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

# A database per customer (draft)

> DRAFT. This launch post is not published. The tenant model is fixed, while
> the scale and unit-economics claims below wait for the launch measurements.

## Outline

- The thesis (D-51): database-per-tenant is a first-class pattern. The launch scale test measures whether thousands to millions of small databases per project are routine when idle.
- The storage model (D-44): one SQLite file per database; idle files are closed and their backup stream paused; the open-file LRU is sized by RAM, not count.
- Tenant = database, no new storage concept (D-51): `ts.tenants.create(key)` inherits placement and key template from a tenant template; tenant-scoped API keys; per-tenant usage rows; per-tenant branching for support.
- Placement lock is residency (D-52): `eu-only`, `br-only`, or an explicit region list; set at creation, never widened; enforced at placement, replication, backup, restore, export, and branch.
- The economics still need a measurement: the launch record covers the Free database abuse guard (100 req/s, 50 GB/month member egress) and idle open-file cost. No dollar estimate belongs here until D-61 is complete.
- What customers get: isolation by scoping rather than noisy shared tables, per-tenant PITR and branching, and a reaper that only ever touches idle Free databases (D-76).
- Closing pattern: the multi-tenant SaaS we recommend, and why tenant ≠ row.

## Draft

Many database services push customers toward a small number of databases because each open database consumes host resources. TerraScale's design makes a database per tenant a first-class pattern (D-51). The launch scale test measures whether a project with thousands, and eventually millions, of small databases remains routine while they are idle. If you are building a SaaS and each customer needs a separate database, the API should make that choice ordinary.

The storage model is one SQLite file per database (D-44). After N minutes with no request, the member closes an idle file and pauses its backup stream. The open-file LRU is sized by resident memory, not by file count. The resulting idle cost is a launch measurement, not a promise in this draft. Storage remains a billed dimension even when a file is closed.

The tenant model is deliberately simple (D-51): a tenant is a database. There is no separate tenancy concept in the storage engine. You define a tenant template with placement and a key template, then `ts.tenants.create(key)` mints a database that inherits it. Tenant-scoped API keys use the same template. Usage is reported per tenant, and support can branch a tenant's database to reproduce a bug without changing production. Everything that applies to a database applies to a tenant because they are the same resource.

Residency is a lock, not a preference (D-52). A database's placement lock, `eu-only`, `br-only`, or an explicit region list, is set at creation and can never be widened. The implementation applies it to placement, replication, backup, restore, export, and branching. The G3 launch gate must prove that a placement-locked database has no bytes at rest outside its regions. For a SaaS with EU or Brazilian customers, per-tenant placement can keep residency policy explicit.

The economics still need evidence. D-64 defines the worst-case Free database abuse guard, including 100 requests per second of direct member reads and 50 GB of monthly member egress. D-61 records the resulting unit cost. Reads are never billed, and the guard, not a price, bounds abuse. Paid plans are never reaped. Under D-76, an idle Free database is paused at 30 days with email notice and deleted at 90 days after two more emails. This draft does not publish a dollar estimate.

The pattern we recommend is tenant ≠ row. Shared-table designs can conserve database resources, but they also put noisy neighbors, cross-tenant query scope, and per-tenant restore in application code. Give each tenant a database, scope access with tenant-scoped keys, and make the storage boundary carry the isolation rule instead of a `WHERE` clause that every query must remember.
