# PostgreSQL driver startup and returning rows

The PostgreSQL wire adapter answers `SHOW transaction isolation level` and
`SHOW transaction_isolation` as `serializable`, the existing SQL session isolation
contract. `SHOW default_transaction_isolation` reports the same value. This adds
session metadata used by SQLAlchemy; it does not change transaction semantics.
`SHOW time zone` is the PostgreSQL spelling of `SHOW TimeZone`, used by pgcli.
Unknown settings remain errors.

`INSERT`, `UPDATE` and `DELETE` accept the relational SQL layer's existing
`RETURNING` column projection through the wire adapter. Prepared statements describe
the returned columns before execution, and affected-row command tags remain intact.
Returning a row does not commit an explicit transaction; rollback discards its writes.

These changes support specific driver workflows. They do not implement arbitrary
PostgreSQL catalogs, savepoints, identity DDL, extensions, COPY or PostgreSQL ORM
migration tooling. ORM guides must identify their actual SQL dialect and transport;
Validation of SQLite-oriented ORMs does not establish PostgreSQL dialect compatibility.
