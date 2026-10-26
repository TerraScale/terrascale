# Defaults, transaction descriptions and finite numbers

`INSERT INTO t DEFAULT VALUES` inserts one row without input values. Each column
takes its declared default or SQL NULL, and an omitted INTEGER PRIMARY KEY is
generated. Normal constraints still apply. `RETURNING` reports the inserted row.

Hrana `describe` uses the current transaction's schema, including uncommitted
CREATE and DROP statements, across requests. Describing a query or a write's
RETURNING columns does not execute the write, commit the transaction, consume a
transaction-log statement, or bill a user read/write operation.

SQL values must be finite numbers, text or NULL. Nonfinite expression results and
Hrana float arguments return `SQLITE_ERROR` with `nonfinite numbers are not
supported`. HTTP JSON arguments already reject nonfinite numbers as invalid
input. Validation happens before result hashing, document serialization or
commit; a failed HTTP batch rolls back all its statements. In an explicit Hrana
transaction, a failed statement leaves earlier successful statements pending
until the client commits or rolls back. Finite numbers keep their existing types.

These changes introduce no storage-format change or migration.
