---
title: Indexes
description: Speed up queries on TerraScale databases with secondary indexes.
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Indexes

TerraScale SQL keeps secondary indexes on ordinary tables. `CREATE INDEX` and `DROP INDEX` are schema statements, so they run like any other write: inside the request's transaction, on the database's home region, and they need the `sql:write` scope. Every index type the SQLite dialect supports is available: plain, `UNIQUE`, partial (`WHERE …`) and expression indexes.

## Create and query

```sql
CREATE TABLE customers (
  id INTEGER PRIMARY KEY,
  email TEXT NOT NULL,
  region TEXT NOT NULL,
  seats INTEGER NOT NULL
);

CREATE UNIQUE INDEX customers_email ON customers (email);
CREATE INDEX customers_region_seats ON customers (region, seats);
CREATE INDEX customers_big ON customers (email) WHERE seats > 100;
```

With the index in place, constrained lookups scan the index instead of the table:

```sql
EXPLAIN QUERY PLAN SELECT id FROM customers WHERE region = 'gru' AND seats >= 10;
```

The plan's detail field names the index used (`customers_region_seats`), the same on every SQL surface — HTTP, WebSocket and the Postgres wire.

Inspect an index with the schema-reading pragmas `PRAGMA index_list(customers)`, `index_info` and `index_xinfo`, or list everything in `sqlite_schema`. Drop it with `DROP INDEX customers_region_seats;`.

## Limits

Indexes follow the SQL request limits: 1 to 256 statements per request, each at most 16 KiB, in a body of at most 1 MiB, with a 5-second deadline per request or transaction. Writes and DDL need the `sql:write` scope; a request mixing reads and schema changes is one transaction, so a refused statement rolls the index change back. See [limits](/zh-cn/limits) and [SQL](/zh-cn/concepts/sql).

## Tip

Use `EXPLAIN QUERY PLAN` before and after adding an index: if the detail field still says `SCAN` on the table rather than `SEARCH … USING INDEX`, the query cannot use it — usually a type mismatch or a leading column that is not constrained.

</div>
