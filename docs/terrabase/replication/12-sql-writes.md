# 12. SQL writes

`POST …/sql` accepts `INSERT`, `UPDATE` and `DELETE` in the `SqlSubset` grammar (SqlParserCS, SQLite dialect). The
same bounds apply as for reads: 16 KiB, 256 conditions, no joins, no subqueries, no functions in `WHERE`. Turso cannot
cancel a statement (section 2), so the grammar stays bounded. Write statements need the `documents:write` scope.

| Statement | Translation |
| --- | --- |
| `INSERT INTO c (_id, f1, f2) VALUES (…), (…)` | one PUT per row. `_id` is optional (a ULID is generated). An `_id` that exists locally is an error (409, local check only; concurrent inserts across regions resolve by LWW) |
| `UPDATE c SET f = <literal>, g = g + <n>, h = <field or arithmetic> WHERE <filter>` | the lane runs the compiled `SELECT id, <expressions> FROM "c" WHERE <filter> LIMIT 1025` on the writer connection, inside the write transaction. Each row becomes `base` writes for literals and computed values, and `f = f ± <integer>` becomes `inc` (a convergent increment). Nested `SET a.b = …` is a read-modify-write of `a` |
| `DELETE FROM c WHERE <filter>` | the same `SELECT id`, then one DELETE per row |

- `WHERE` sees the local state at write time. Results are eventually consistent, as for API writes.
- One statement is one operation with one stamp. It is atomic up to 1024 documents. Up to 10,000 it commits in chunks
  of 1024, each atomic with its own stamp, and the statement as a whole is not atomic. More than 10,000 rows is 400
  `sql_too_many_rows`.
- Response: `{"affected": n, "ids": [... up to 1000]}`. No `RETURNING` in v1.
- Rejected: `INSERT … SELECT`, `ON CONFLICT`, `REPLACE`, DDL, `PRAGMA`, `ATTACH`, and writes to `_tb_*` or reserved
  collections.

Execute-and-capture was considered as an alternative: run arbitrary DML in a savepoint, read its CDC rows, roll back,
and replay them as lattice operations. It would give full SQL, but it depends on unbounded statements and on
untested savepoint-plus-CDC behaviour. It is a candidate after cancellation exists in Turso.
