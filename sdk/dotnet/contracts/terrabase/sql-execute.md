# Relational SQL execute

Producer contract for `TerraBaseDatabase.ExecuteSqlAsync`, `QuerySqlAsync` and `ExecuteSqlBatchAsync`, from
`src/TerraBase/Features/Sql/SqlExecuteEndpoint.cs` and `SqlModels.cs` at `TerraScale/terrascale@1a3a7a72e`
(operation `executeSql`). Public client listener, ts-auth project access token (`Authorization: Bearer …`).

`POST /v1/projects/{project}/databases/{database}/sql/execute`, `Content-Type: application/json`, body at most 1 MiB:

```json
{"statements":[{"sql":"INSERT INTO people (id, name) VALUES (?, :name)","args":[1],"named":{"name":"Ada"}}]}
```

- 1 to 256 statements, each at most 16 KiB. `args` and `named` are optional; values are null, finite numbers,
  booleans (stored as 1 or 0) or strings. `named` keys omit the `:`, `@` or `$` prefix.
- Every request is one transaction, all or nothing. `BEGIN`, `COMMIT` and `ROLLBACK` are refused.
- Scope `documents:read`; writes and DDL also need `documents:write`.

200 answers one result per statement, in order:

```json
{"results":[{"columns":[{"name":"id","decltype":"INTEGER"}],"rows":[[1,"Ada"]],"rows_affected":0,"last_insert_rowid":null}]}
```

`decltype` is null for an expression. Cells are null, numbers or strings; integers are JSON numbers, so
JavaScript readers lose precision beyond 2^53.

A refused statement rolls everything back and answers
`{"error":{"code","message","statement","sqlite_code"}}` with `Cache-Control: no-store`:
400 `sql_error`, 403 `forbidden_scope` (`SQLITE_AUTH`), 409 `sql_conflict` (`SQLITE_BUSY`) or 413 `sql_too_large`
(`SQLITE_TOOBIG`). The SDK throws `TerraBaseSqlException` for these. Request validation (`invalid_request`, with
`op` naming the statement for a bad argument), 401, 404, 413 body limits, 415, 429 and 503 use the generic
`{"error":{"code","message"}}` shape and the existing `TerraBaseException` subclasses. Nothing is retried.
