# A database per customer

> DRAFT — launch post outline + draft paragraphs. Not published.

## Outline

- The thesis (D-51): database-per-tenant is a first-class pattern — thousands to millions of small databases per project must be routine and near-free when idle.
- The substrate (D-44): one SQLite file per database; idle files are closed and their backup stream paused; the open-file LRU is sized by RAM, not count.
- Tenant = database, no new storage concept (D-51): `ts.tenants.create(key)` inherits placement and key template from a tenant template; tenant-scoped API keys; per-tenant usage rows; per-tenant branching for support.
- Placement lock is residency (D-52): `eu-only`, `br-only`, or an explicit region list; set at creation, never widened; enforced at placement, replication, backup, restore, export, and branch.
- The economics that make it honest: a Free database under its worst-case abuse guard (100 req/s, 50 GB/month member egress) costs around $1.5/month — idle costs ~0.
- What customers get: isolation by scoping rather than noisy shared tables, per-tenant PITR and branching, and a reaper that only ever touches idle Free databases (D-76).
- Closing pattern: the multi-tenant SaaS we recommend, and why tenant ≠ row.

## Draft

Most databases-as-a-service make you fit your product into a small number of databases, because each one is expensive for them to host. TerraScale inverts that (D-51): a database per tenant is a first-class pattern, and a project with thousands — eventually millions — of small databases must be routine. If you're building a SaaS and every one of your customers deserves their own database, that should be an ordinary Tuesday, not an enterprise conversation.

The substrate makes it possible (D-44). Every database is one SQLite file, and a file nobody is touching costs essentially nothing: after N minutes with no request, the member closes it and pauses its backup stream. The open-file LRU is sized by resident memory, not by file count, so thousands of idle free-tier databases and dev branches cost ~0. Idle isn't a discounted state; it's off.

The tenant model is deliberately un-clever (D-51): a tenant is a database. There is no separate tenancy concept bolted onto the storage engine. You define a tenant template — placement, key template — and `ts.tenants.create(key)` mints a database that inherits it. Tenant-scoped API keys are minted from the same template. Usage is reported per tenant, and support can branch a single tenant's database to reproduce a bug without touching production. Everything you already know about databases applies to tenants, because they are the same thing.

Residency is a lock, not a preference (D-52). A database's placement lock — `eu-only`, `br-only`, or an explicit region list — is set at creation and can never be widened. It is enforced at every point bytes could move: placement, replication, backup, restore, export, and branching. A placement-locked database provably has no bytes at rest outside its regions. For a SaaS with EU or Brazilian customers, that per-tenant placement is often the feature that closes the deal.

The economics are honest rather than magical. The worst case for a Free database sits inside its abuse guards (D-64): 100 requests per second of direct member reads and 50 GB of monthly member egress lands around $1.5/month, and typical tenants cost far less — the reactive path serves readers from the CDN, where bandwidth is free. Reads are never billed; the guard, not a price, is what bounds the worst case. Paid plans are never reaped; the only database that ever disappears is an idle Free one — paused at 30 days with email notice, deleted at 90 after two more emails (D-76).

The pattern we recommend, then: tenant ≠ row. Multi-tenant shared-table designs exist to conserve databases; when databases are free at rest, keeping tenants in shared rows buys you nothing and costs you everything — noisy neighbors on hot indexes, cross-tenant leakage risk in every query, no per-tenant restore. Give each tenant a database, scope access with tenant-scoped keys, and let isolation be a property of the storage boundary instead of a `WHERE` clause you have to remember.
