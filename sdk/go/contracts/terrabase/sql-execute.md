# SQL over REST

Producer contract for `Database.Execute`, `Query`, `QueryAs` and `Batch`, from
`src/TerraBase/Features/Sql/SqlExecuteEndpoint.cs`, `SqlBatch.cs`, `SqlModels.cs` and `SqlErrors.cs` (PIVOT E3,
main at `2020a18ee`; operation `executeSql`). Bearer API key or project access token.

`POST /v1/projects/{project}/databases/{database}/sql/execute`, `Content-Type: application/json`, body at most 1 MiB:

```json
{"statements":[{"sql":"INSERT INTO people (id, name, avatar) VALUES (?, :name, ?)","args":["01JA0000000000000000000001",{"base64":"AQID"}],"named":{"name":"Ada"}}]}
```

- 1 to 256 statements, each at most 16 KiB. `args` and `named` are optional; values are null, finite numbers,
  booleans (stored as 1 or 0), strings or blobs `{"base64":"…"}`. `named` keys omit the `:`, `@` or `$` prefix.
- Every request is one transaction, all or nothing. `BEGIN`, `COMMIT` and `ROLLBACK` are refused
  (`transaction_state`); `SAVEPOINT`, `RELEASE` and `ROLLBACK TO` are accepted.
- Scope `sql:read`; writes and DDL also need `sql:write`.

200 answers one result per statement, in order:

```json
{"results":[{"columns":[{"name":"id","decltype":"TEXT"},{"name":"avatar","decltype":"BLOB"}],"rows":[["01JA0000000000000000000001",{"base64":"AQID"}]],"rows_affected":0,"last_insert_rowid":null}]}
```

`decltype` is null for an expression. Cells are null, numbers, strings or blobs `{"base64":"…"}`.

A refused statement rolls everything back and answers `{"error":{"code","message","statement","sqlstate"}}` with
`Cache-Control: no-store`. Codes, SQLSTATEs and statuses are in [sql/error-codes.json](sql/error-codes.json). The
SDK returns `*v1.SQLError` for every body that names a statement and a SQLSTATE, resends
`409 serialization_conflict` at most twice (the request rolled back), and sends everything else once. Failures
before execution (401, 404, 421, 415, 503) use `{"error":{"code","message"}}` and the generic `*v1.APIError`
values.
