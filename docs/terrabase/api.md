# HTTP API

TerraBase serves SQL databases under `/v1/projects/{project}/databases/{database}`. Use the generated `/openapi/v1.json` and Scalar at `/scalar/` for the complete current route and body schemas. They are generated from endpoint classes, not a second handwritten route inventory.

## SQL

| Method | Suffix | Contract |
|---|---|---|
| POST | `/sql/execute` | One atomic batch of 1–256 SQL statements |
| GET | `/sql/ws` | SQL WebSocket upgrade, subprotocol `terrascale.sql.v1` |
| GET | `/shape` | SQL table snapshot, catch-up and live changes |
| GET | `/export` | Streams the database as one SQL dump |

`/export` needs `sql:read` and streams schema, rows, indexes, views and triggers from one read snapshot, wrapped in a single transaction; a mid-stream failure aborts the connection rather than returning a partial file. The home region serves it.

[Native SQL](sql/native-sql.md) specifies types, parameter binding, limits and errors. [Shapes](shape-reads.md) specify their parameters and continuations. PostgreSQL drivers use the TLS PostgreSQL listener and the same SQLite dialect.

SQL requires `sql:read`; writes and DDL also require `sql:write`. End-user tokens can read authorized shapes through [table read grants](shape-read-grants.md). They do not grant general SQL or management access.

## Management

[Management](management-api.md) owns database lifecycle, placement, backups, recovery and usage. Identity, organizations, projects and customer API credentials belong to ts-auth. Dashboard owns its UI, plans, subscriptions and billing. Each service calls the owning service's API.

Project/database authorization is checked before returning tenant facts. A valid credential outside its resource boundary receives a non-disclosing refusal; see [cross-project errors](cross-project-errors.md). An unresolved storage authority fails closed.

## Regions

Discover regions through the published catalog rather than embedding regional hosts. The responding region appears in `X-TerraBase-Region`. Region pins and mismatches follow [region identity](region-identity.md).

Reads use a ready regional replica where supported. Writes follow [write routing](sql/write-routing.md); a request to another region can be forwarded to home. A routing decision is separate from its actual commit location. SQLSTATE `40001` requires retrying the whole transaction. [Home apply](home-apply.md) defines accepted and rejected transaction states.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

## Errors and observations

HTTP SQL errors contain `error.code`, `message`, `statement` and `sqlstate`. Errors do not expose engine names, credential values or statement text. Every response carries `X-Request-ID`; see [request identity](request-identity.md). Authentication and authorization failures preserve their HTTP status and challenge headers.

SQL reads are free with plan fair use. Committed writes count toward monthly write units; replication and shape reads do not. See [operation allowances](operation-allowance.md).
