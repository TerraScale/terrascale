# SQL editor completion catalog

The editor uses SQL definitions, including empty tables. Metadata comes from the SQL schema rather than sampled values.
The first POST to `/v1/projects/{project}/databases/{name}/sql/execute` reads
`SELECT name FROM sqlite_master WHERE type='table' ORDER BY name LIMIT 101`.
At most 100 names become one second read-only batch of `PRAGMA table_info('escaped name')` statements.
No per-table HTTP requests or DDL parsing occur. Types come from the PRAGMA row's `type`, not result `decltype`.
More than 100 tables produces an explicit truncation notice; no total count is inferred.
Empty declared types are labeled accordingly. Missing metadata fails the catalog snapshot, preserving query results.

The existing SQL HTTP bridge (TS-907) shares source-generated JSON, current-user project scopes, canonical names,
region pins and an 8 MiB response bound. Catalog work additionally has a 10-second deadline across both requests.
Each HTTP request is its own transaction; the second batch shares one read-only snapshot. Concurrent DDL can
invalidate the earlier name list, so refresh is available and retains SQL/history. Completion quotes identifiers.
No API, SDK or storage contract changes or migrations are required. Embedded completion JSON now names SQL tables.


A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
