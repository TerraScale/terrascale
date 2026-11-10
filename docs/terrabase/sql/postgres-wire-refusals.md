# Postgres wire: catalog detection and refused commands

- A statement is a catalog query only when it names `pg_catalog.` or `information_schema.`, or reads an unqualified
  PostgreSQL catalog relation or view (`pg_class`, `pg_type`, `pg_namespace`, `pg_stat_*` and the other built-in names).
  Catalog queries outside psql's `\d`, `\dt`, `\l` and `\dn` get `0A000`. A user table whose name only starts with
  `pg_` (for example `pg_orders`) is ordinary SQL over the wire, as it is over `/v1` SQL execution. A user table named
  exactly like a catalog relation is shadowed, as in PostgreSQL, where `pg_catalog` is searched first.
- `LISTEN`, `NOTIFY`, `UNLISTEN` and `CREATE`/`ALTER`/`DROP EXTENSION` get `0A000` (feature not supported), not a
  syntax error.
- `FULL JOIN` and `FULL OUTER JOIN` get `0A000` with `full outer join is not supported; use LEFT JOIN with UNION ALL`
  before execution, on every surface (REST, WebSocket and the wire).
- `BEGIN`/`START TRANSACTION` access modes are honored: `READ ONLY` opens a read-only transaction whose writes fail
  with `25006` (`read_only_sql_transaction`), while reads continue and `COMMIT`/`ROLLBACK` end it normally; `READ WRITE`
  and an unmodified `BEGIN` are unchanged. Isolation levels are accepted and ignored (transactions are serializable).
  `SET TRANSACTION` is not supported: it falls through to the SQL layer's existing refusal.
- `SET TimeZone` accepts only UTC and its aliases (`UTC`, `Etc/UTC`, `Etc/UCT`, `UCT`, `GMT`, `Etc/GMT`, `Z`, `Zulu`,
  `Universal`, `Etc/Universal`, `Greenwich`); anything else gets `22023` (`invalid_parameter_value`) and `SHOW TimeZone`
  stays `UTC`. `SET DateStyle` accepts ISO forms (`ISO`, `ISO, MDY`, `ISO, DMY`, `ISO, YMD`) the same way; `SHOW DateStyle`
  stays `ISO, MDY`. `SET … TO DEFAULT` and `RESET TimeZone`/`RESET DateStyle` restore those defaults. Times always render
  in UTC and dates in ISO regardless (see [temporal types](../postgres-temporal-types.md)).
- `INSERT`/`UPDATE`/`DELETE … RETURNING` is supported over the wire (see `native-sql.md`).
- A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
