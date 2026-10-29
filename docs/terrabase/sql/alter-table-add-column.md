# Adding a nullable SQL column

`ALTER TABLE table_name ADD [COLUMN] column_name type [NULL | DEFAULT NULL]`
adds one nullable column through SQL HTTP, Hrana or Postgres. This supersedes
the earlier blanket refusal of ALTER TABLE in the relational SQL design.
Existing rows read the new column as NULL without a row rewrite. Inserts and
updates can then supply it. The column is last in `SELECT *` and
`PRAGMA table_info`; `sqlite_master.sql` includes its declaration.

The operation needs `documents:write`, participates in transactions and uses
the existing replicated collection metadata. It creates no index. Duplicate
names (case insensitive), more than 256 columns, document collections and
missing tables are refused. NOT NULL, non-null defaults, SERIAL, constraints,
COLLATE, IF EXISTS, IF NOT EXISTS, multiple additions, RENAME and DROP COLUMN
are unsupported. A declared type may be omitted where the SQLite parser
accepts it.

The whole table definition follows last-writer-wins replication: concurrent
additions in different regions do not merge. Wait for the definition to arrive
before using the new column in another region. A row can arrive first; its
field is retained but SQL cannot name it until the definition arrives.
Document fields already bearing the new name become visible unchanged;
there is no affinity backfill. Concurrent document schema or TTL edits can
also conflict with the metadata rewrite.

Postgres refuses a prepared result whose column count changed with SQLSTATE
`0A000` (`cached plan must not change result type`); prepare the query again.
No storage format or peer protocol changes. Older nodes refuse ALTER execution
but understand the existing table-definition shape; mixed-version runtime
verification is pending.
