# Changelog

## Unreleased

- Added the optional `sqlalchemy` extra (`pip install terrascale[sqlalchemy]`): a
  `terrascale://<project_id>/<database>` SQLAlchemy dialect and PEP 249 DBAPI
  over the SQL HTTP API, running in autocommit — each statement is one request
  and a multi-row insert is one batched request. Multi-statement transactions
  are not supported through the dialect yet.
