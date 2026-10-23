# SQL indexes and batch writes

Implements the `CREATE [UNIQUE] INDEX` and `DROP INDEX` row of [relational-sql.md](relational-sql.md) section 2.

## Statements

- `CREATE [UNIQUE] INDEX [IF NOT EXISTS] name ON table (col [ASC|DESC], …)`: 1 to 4 columns of a SQL table. Partial
  (`WHERE`), expression, `USING`, `INCLUDE` and `CONCURRENTLY` indexes are refused.
- `DROP INDEX [IF EXISTS] name`: drops an index of a SQL table. Indexes of document collections belong to the indexes
  API and answer `no such index`.
- Names are lowercased: a letter, then letters, digits or `_`, at most 63 characters. As in SQLite they are unique in
  the database; a name a table's `UNIQUE` column already uses (`sql_unique_N`) is taken too.

## Translation

- Every index is a secondary index of the table's collection (the index service's storage), so its name is
  database-wide and `ListIndexes` shows it. SQL reads do not use it yet (section 4 of the design: reads scan).
- `UNIQUE` also declares a unique key of the same name on the collection, so a duplicate write answers
  `SQLITE_CONSTRAINT_UNIQUE` (Postgres `23505`). A table with rows is reindexed in the same transaction, the
  projection a replica runs when the declaration arrives; rows that already hold a duplicate refuse the statement with
  `SQLITE_CONSTRAINT_UNIQUE` and nothing changes. The documents API keeps its own rule: declaring keys there still
  needs an empty collection (`schema_requires_empty_collection`).
- `DROP INDEX` of a unique index removes its key and reindexes, so rows a concurrent region's write hid under it show again.

## Batch writes

A request's statements share one storage transaction, and each storage call is a hop to the database's worker thread.
The SQL layer keeps those hops to the ones a write needs:

- a request parses each distinct statement text once, and a session reuses its last parse, so a batch of one
  `INSERT` repeated with new arguments parses it once;
- a transaction reads each table definition once (DDL of the table clears it);
- a plain `INSERT` relies on its create-only precondition instead of reading the row first;
- storage accounting reads no bytes it knows: none before a create-only write, none after a delete, and a patch's
  stored document after it.

An `INSERT` row now costs the document service's version read and put plus one size read; an `UPDATE` row the size
read, the patch and its read-back. Measured with `benchmark/scripts/sql-batch.py` (results in `benchmark/bench/`).
