# Changelog

## Unreleased

- `execute(Statement(...), args)`, and `query` called that way, on the client,
  the async client and both SQL sessions raise `TypeError` before any request:
  a `Statement` carries its own arguments. Earlier versions dropped `args`.
- Added the optional `sqlalchemy` extra (`pip install terrascale[sqlalchemy]`): a
  `terrascale://<project_id>/<database>` SQLAlchemy dialect and PEP 249 DBAPI
  over the SQL HTTP API, running in autocommit — each statement is one request
  and a multi-row insert is one batched request. The API key is read from
  `TERRASCALE_API_KEY` or supplied as the URL password, never a query
  parameter. The dialect now defaults to real transactions over the SQL
  WebSocket session (implicit begin, commit/rollback, `begin_nested()`
  savepoints), limited to 5 seconds each by the server;
  `isolation_level="AUTOCOMMIT"` keeps the previous one-request-per-statement
  HTTP path.
- Alembic migrations run through the dialect: the `terrascale` dialect name is
  registered with Alembic's SQLite implementation when Alembic is installed, so
  `render_as_batch=True` table-copy migrations and autogenerate reflection work
  over the SQL API. Documented in the Use SQLAlchemy and Alembic guide.
