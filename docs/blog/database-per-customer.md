# A database per customer (stub)

> L-5 launch-post stub. Product reference: D-51 tenants, D-44 idle close.

- Tenant = database: `ts.tenants.create(key)` mints a database from a tenant
  template with inherited placement and key template.
- Thousands to millions of small databases per project; idle databases close
  and cost ~0. Per-tenant branching, keys, and usage rows included.
- Cross-tenant admin queries are post-launch; this post will show the
  per-tenant patterns that work at launch.
