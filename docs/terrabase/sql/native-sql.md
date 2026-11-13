# Native SQL

Each customer database holds real SQL tables using the SQLite dialect. `CREATE`, `ALTER` and `DROP TABLE`, indexes (including unique, partial and expression indexes), views and triggers run through the database engine. Foreign keys are always enforced, including `ON DELETE CASCADE`. JSON values use `TEXT` and the native JSON functions.

## Surfaces

| Surface | Contract |
|---|---|
| HTTP | `POST /v1/projects/{project}/databases/{database}/sql/execute` |
| WebSocket | `GET …/sql/ws`, subprotocol `terrascale.sql.v1` |
| PostgreSQL | TLS PostgreSQL listener, SQLite statements |

All surfaces share SQL validation, types and error mapping. HTTP requires `sql:read`; writes and DDL also require `sql:write`. End-user shape grants do not authorize SQL execution. See [TLS and authentication](../transport-security.md).

HTTP accepts `{"statements":[{"sql":"SELECT ?","args":[1]}]}`. Each statement may also bind named parameters through `named`. Values are null, integers, floating-point numbers, strings and blobs. HTTP represents a blob as `{"base64":"…"}`; the PostgreSQL surface uses `bytea`. Booleans bind as 1 or 0.

Each result has `columns`, `rows`, `rows_affected` and `last_insert_rowid`. SQL failures carry a TerraScale `code`, safe `message`, failing `statement` index and PostgreSQL `sqlstate`; HTTP wraps these under `error`.

## Transactions and limits

- One HTTP request is one atomic batch of 1–256 statements, all or nothing. `BEGIN`, `COMMIT` and `ROLLBACK` are refused; savepoint statements are supported.
- A PostgreSQL simple-protocol Query message with several statements runs as one implicit transaction: a failed statement rolls back the whole message. A message that itself contains `BEGIN`, `COMMIT` or `ROLLBACK` runs statement by statement instead. Single-statement messages and the extended protocol are unchanged.
- WebSocket transaction frames and PostgreSQL transactions pin one session. Transactions live at most five seconds, then roll back and release the writer. See [WebSocket](websocket.md).
- A failed statement inside an explicit transaction uses the session's savepoint/error rules; PostgreSQL aborted transactions require rollback or rollback to a savepoint.
- Statement text is at most 16 KiB, the HTTP body at most 1 MiB, results at most 10,000 rows and 8 MiB, and a transaction changes at most 1,024 rows or grows the file by at most 8 MiB.
- Native query cancellation and value-length bounds apply before large results are materialized. Request cancellation retains resource ownership until the running native work drains.

A batch runs one parsed statement at a time, so later statements see earlier DDL. The parser supplies boundaries; a single-statement call refuses trailing statements. Every write holds the organization's storage-quota lease before the database writer and retains it through commit or rollback.

## Sandbox

Every surface applies the same sandbox before execution. It respects SQL quoting and comments and checks view and trigger bodies.

Refused operations include `ATTACH`, `DETACH`, `VACUUM`, `ANALYZE`, temporary/virtual tables, extension loading, file access functions and page-inspection tables. Schema-reading PRAGMAs and the enforced foreign-key setting are the supported PRAGMA boundary. Customer SQL cannot name internal tables. Catalog listings and introspection filter them out, and reserved objects return a non-disclosing refusal. The implementation's reserved-name list is authoritative; callers must not rely on internal prefixes or names.

Schema listings expose only customer objects. A table or column mentioned in an error is never permission to inspect another tenant. Statement text, bound values and engine diagnostics are not echoed into errors.

## Regions and conflicts

[Write routing](write-routing.md) selects local or home execution from SQL structure, schema facts and runtime readiness. A routing preference alone does not prove a local commit. A read-then-write transaction that cannot retain its original snapshot fails with `serialization_conflict` (`40001`); retry the whole transaction.

[Home apply](../home-apply.md) owns transaction acceptance and HLC column conflict resolution. [Regional replicas](../region-replicas.md) and [local overlay](../local-overlay.md) describe regional reads, pending writes and home durability.

## Row guards

`terrascale_expect_changes(changes(), n)` returns the previous statement's changed-row count when it is `n` and
otherwise fails the statement with `409 row_conflict` (SQLSTATE `P0001`), which rolls the request back like any failed
statement and aborts an open transaction. Put it right after an `UPDATE` or `DELETE` guarded by a version column to get
optimistic concurrency with a code clients can classify:

```sql
UPDATE accounts SET email = ?, version = ? WHERE id = ? AND version = ?;
SELECT terrascale_expect_changes(changes(), 1);
```

## Errors

| Code | SQLSTATE |
|---|---|
| `syntax_error` | 42601 |
| `undefined_table`, `undefined_column` | 42P01, 42703 |
| `primary_key_violation`, `unique_violation` | 23505 |
| `not_null_violation`, `check_violation`, `foreign_key_violation` | 23502, 23514, 23503 |
| `forbidden_scope`, `sql_sandbox` | 42501 |
| `serialization_conflict` | 40001 |
| `row_conflict` | P0001 |
| `too_large`, `query_canceled` | 54000, 57014 |
| `write_outcome_unknown` | 08007 (HTTP 503; reconcile before repeating) |
| `transaction_rolled_back`, `transaction_aborted` | 40000, 25P02 |
| `read_only_transaction`, `transaction_state` | 25006, 25000 |
| `quota_exceeded`, `tenant_busy` | 53100, 53300 |
| `unauthorized`, `unavailable` | 28000, 57P03 |
| `write_outcome_unknown` | 08007 |

The served [API reference](../api-reference.md) includes HTTP statuses and the complete response schemas.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

## Metering and storage

SQL reads are free with monthly fair use. Shape reads are free and unmetered. Committed writes bill the larger of changed rows and started 4 KiB of database growth; rollback and refused statements add no write units. See [operation allowances](../operation-allowance.md).

SQL storage counts toward the organization quota. The used-page delta is checked at commit and persisted in `_tb_sql_storage`; SQL commit offsets invalidate cached accounting. [Continuous backup](../backup.md) captures committed SQL changes, including schema changes, for recovery.
