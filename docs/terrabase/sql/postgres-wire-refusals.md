# Postgres wire: catalog detection and refused commands

- A statement is a catalog query only when it names `pg_catalog.` or `information_schema.`, or reads an unqualified
  PostgreSQL catalog relation or view (`pg_class`, `pg_type`, `pg_namespace`, `pg_stat_*` and the other built-in names).
  Catalog queries outside psql's `\d`, `\dt`, `\l` and `\dn` get `0A000`. A user table whose name only starts with
  `pg_` (for example `pg_orders`) is ordinary SQL over the wire, as it is over `/v1` SQL execution. A user table named
  exactly like a catalog relation is shadowed, as in PostgreSQL, where `pg_catalog` is searched first.
- `LISTEN`, `NOTIFY`, `UNLISTEN` and `CREATE`/`ALTER`/`DROP EXTENSION` get `0A000` (feature not supported), not a
  syntax error.
- `INSERT`/`UPDATE`/`DELETE … RETURNING` is supported over the wire (see `native-sql.md`).
