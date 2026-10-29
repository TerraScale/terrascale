# SQL point reads by a TEXT primary key

- Status: implemented, 2026-10-27. Narrows the shortcut of [relational-sql.md](relational-sql.md) section 4 ("every read
  scans each referenced table") for the most common ORM shape, without changing any result.

## Rule

A read whose statement is one `SELECT` of one table, with no join, subquery or own `WITH`, where the table has a single
`TEXT` primary key `k` and the `WHERE` holds `k = <value>` (either side) among its top-level `AND` terms, `<value>` a
parameter or a `'…'` literal without quote characters, reads only that row. The table's CTE gets the extra filter
`id = <value>` on the collection table's indexed document id, before its scan guard:

```
WITH "t" AS (SELECT id AS "k", json_extract(doc, '$.c') AS "c" FROM main."t" WHERE id = $p1 AND tb_qos_scan(octet_length(doc)))
SELECT … FROM t WHERE tb_sql_row(…) AND (k = $p1 …)
```

`UPDATE` and `DELETE` read the rows they change through the same `SELECT`, so `UPDATE t SET … WHERE k = ?` and
`DELETE FROM t WHERE k = ?` are index-served too.

## Why results cannot change

- The statement keeps its own `k = <value>` term; the CTE filter can only drop rows that term drops.
- For a single `TEXT` key the CTE's key column is the document id itself (`id AS "k"`), so both compare the same stored
  text with the same `TEXT` affinity and the default `BINARY` collation. A term with `COLLATE`, an `OR`, or a value that is
  an expression is not used.
- The table is the statement's only table reference, so no other reference reads the filtered CTE.

## Cost and metering

The read scans one candidate row instead of all of them, so the work budget and the plan's scan limits charge that row.
Billing is unchanged: SQL reads bill their result rows ([metering-boundaries.md](metering-boundaries.md)).

## Not covered

`INTEGER` and composite keys keep the scan: their key column is `json_extract` of a stored field, and a document written
through the document API may hold a field that differs from its id, so an id filter could drop a row the statement
returns. Joins, `IN (…)`, ranges and secondary columns also keep the scan until columns are rewritten over `json_extract`
(relational-sql.md section 4).
