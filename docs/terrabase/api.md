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

SQL requires `sql:read`; writes and DDL also require `sql:write`. End-user tokens read shapes and rows only as the database's [rules](rules.md) allow. They get no SQL or management access.

## Rules

| Method | Suffix | Scope | Contract |
|---|---|---|---|
| GET | `/rules` | `databases:read` | The deployed rules with `ETag: "<version>"` and the current `problems`; `404 no_rules` |
| PUT | `/rules` | `databases:write` | Deploys a whole rules set; needs `If-Match` or `If-None-Match: *` |
| GET | `/rules/versions?after=&limit=` | `databases:read` | The last 100 versions, newest first |
| GET | `/rules/versions/{version}` | `databases:read` | One version's rules; `404 unknown_version` |
| POST | `/rules:test` | `databases:read` | Simulates a request against deployed or draft rules without reading or writing rows; reports the deciding clause, readable columns and draft problems ([section 8](rules.md#8-simulator)) |
| POST | `/rules/versions/{version}/restore` | `databases:write` | Deploys that version's body as a new version |

| GET | `/tables/{table}/rows/{key}?columns=` | `sql:read` | One row as `{"row":{…}}`; an end user gets only rows `read` allows and its readable columns; `404 row_not_found` |

[Rules](rules.md) section 7 specifies the routes, preconditions and storage; section 4 the end-user reads. A deploy compiles every condition against the current
schema and answers `400 invalid_rules` with every problem in `details.problems`; `PUT` and restore run in the home region
and answer `421 wrong_region` elsewhere.

## Rows

| Method | Suffix | Scope | Contract |
|---|---|---|---|
| POST | `/tables/{table}/rows` | `sql:write` | `{"row":{…}}`; `201 {"key","row"}` |
| PATCH | `/tables/{table}/rows/{key}` | `sql:write` | `{"row":{…}}` with the columns to change; `200 {"key","row"}` |
| DELETE | `/tables/{table}/rows/{key}` | `sql:write` | `204` |
| POST | `/rows:batch` | `sql:write` | `{"operations":[…]}`, 1 to 100, one transaction; `200 {"results":[…]}` |

`key` is the row id. Values are null, numbers, booleans (1 or 0), strings and `{"base64":"…"}` blobs, in a UTF-8 JSON body of
at most 1 MiB. Creates use `INSERT OR ABORT` and updates `UPDATE OR ABORT`, never an upsert or replace. A tenant end user is
checked by the database's [rules](rules.md) section 5.2 in the home write transaction, and the answer's `row` is cut to the
rule's `columns`, or `null` when `read` is false for it; project credentials write every row and column and receive the whole
row. A region holding a replica forwards the write to the home; any other region answers `421 wrong_region` with nothing
applied. Errors are `{"error":{"code","message","details"}}`: `400 unknown_column`, `constraint_failed`, `validation_failed`
(`details.clause`) or `invalid_request`; `403 rule_denied` (`details.operation`, `details.table`), `column_not_writable`
(`details.column`) or `rules_invalid`; `404 row_not_found`, `unknown_table` or `unknown_database`; `409 row_exists`
(`details.key` when the caller may read that row). A failed batch answers its first failing operation's error with
`details.index` and applies nothing; a foreign key failure found only at commit, or a transaction the engine ended, has no
`details.index`. Writes count against the monthly write allowance like SQL writes.

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
