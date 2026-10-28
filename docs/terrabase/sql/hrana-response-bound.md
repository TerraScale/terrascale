# Hrana response bound

A Hrana HTTP request (`pipeline` or `cursor`) returns at most an estimated 16 MiB of results
(`HranaExecutor.MaxResponseBytes`).

- The SQL session asks the Hrana executor to admit each statement's result **before** the statement takes effect:
  before an autocommit write commits, before a transaction logs the statement, and before `BEGIN`, `COMMIT` or
  `ROLLBACK` acts.
- The statement whose result crosses the bound fails with `SQLITE_TOOBIG` and has no effect: an autocommit write is
  rolled back, a statement inside a transaction is undone (the transaction stays open), and a refused `COMMIT` commits
  nothing. Every later statement of the request fails with `SQLITE_TOOBIG` without running.
- A successful write therefore always carries the result it asked for (its `RETURNING` rows included), and a write
  that reports `SQLITE_TOOBIG` never committed, so a retry cannot duplicate it.
- A `sequence` returns no rows; it runs only while the request is within the bound.
