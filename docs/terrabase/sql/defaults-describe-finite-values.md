# Defaults and finite numbers

`INSERT INTO t DEFAULT VALUES` inserts one row without input values. Each column
takes its declared default or SQL NULL, and an omitted INTEGER PRIMARY KEY is
generated. Normal constraints still apply. `RETURNING` reports the inserted row.

SQL values must be finite numbers, text or NULL. Nonfinite expression results
return `SQLITE_ERROR` with `nonfinite numbers are not supported`. HTTP JSON arguments already reject nonfinite numbers as invalid
input. Validation happens before result hashing, document serialization or
commit; a failed HTTP batch rolls back all its statements. Finite numbers keep their existing types.

These changes introduce no storage-format change or migration.
