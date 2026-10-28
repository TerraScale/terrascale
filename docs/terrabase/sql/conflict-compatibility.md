# INSERT conflict compatibility

This narrows the conflict behavior described in [relational SQL](relational-sql.md).
The HTTP SQL endpoint and Hrana v2/v3 share this implementation. SQLite references:
[conflict algorithms](https://www.sqlite.org/lang_conflict.html) and
[UPSERT](https://www.sqlite.org/lang_upsert.html).

| Form | Local statement behavior |
| --- | --- |
| Plain `INSERT`, `INSERT OR ABORT` | Duplicate primary/unique keys or missing NOT NULL values fail the statement. |
| `INSERT OR IGNORE` | Skip input rows violating primary, UNIQUE or NOT NULL constraints; continue with later rows. |
| `INSERT … ON CONFLICT DO NOTHING` | Skip primary/UNIQUE violations, but NOT NULL violations fail. |
| `INSERT … ON CONFLICT (columns) DO NOTHING` | Skip only when the named primary/unique key conflicts; unrelated violations still fail. A matching conflict skips the row even if another key also conflicts. |
| `REPLACE`, `INSERT OR REPLACE` | Delete other documents holding conflicting UNIQUE keys, then put the new row. An existing primary-key document is overwritten. Explicit NULL in a NOT NULL column uses its literal default, or fails if none supplies a value. |

Targets must name the complete primary key or an existing UNIQUE constraint/index.
Column order and identifier case do not change the match; duplicate target columns,
nonunique columns and absent columns fail with `SQLITE_ERROR`, including for empty input.
Only ordinary column-list targets are supported. Named constraints, expressions,
partial-index predicates, `DO UPDATE`, `OR FAIL`, `OR ROLLBACK`, and combining an
`OR` algorithm with `ON CONFLICT` are refused rather than approximated.

Conflict lookup reads the engine's canonical uniqueness keys before writes, including
retained hidden contenders. It does not catch a failed write and continue in a faulted
transaction. Replacement deletes use the document service, including authorization,
storage accounting and ordinary write limits. The existing atomic transaction/rollback
and replay rules apply. No storage format or replication protocol changes are required.

`rows_affected` counts inserted/replaced input rows, excluding replacement deletes and
skipped rows; `RETURNING` includes only inserted rows. `last_insert_rowid` is the last
inserted integer key, or null when nothing inserts, per the existing SQL result contract.
Lookup retains at most 1,025 constraint/id pairs per input row; exceeding 1,024 fails
with `SQLITE_TOOBIG`. Replacement deletes also consume the transaction's write budget.

This is a bounded INSERT subset, not full SQLite/Turso compatibility. UPDATE conflict
algorithms, triggers, SQL CHECK constraints, and enforced foreign keys remain unsupported.
Collection-schema admission errors are not ignored. Uniqueness remains the existing
leaderless document constraint model: this change adds no cross-region uniqueness or
SQLite rowid-allocation guarantee. Custom document normalization constraints cannot be
selected as ordinary SQLite column-list targets. No SDK request/response shapes change.
Conflict algorithms explicitly refuse a candidate whose string `id` would be elided
from its document body while a UNIQUE key also contains that field; the existing
document uniqueness projection cannot represent that SQL key faithfully.

Regression coverage is in `tests/TerraBase.Fast/Sql/SqlConflictTests.cs`, using the real
HTTP SQL and Hrana listeners.
