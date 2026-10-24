# SQL batch writes (2026-10-23)

`benchmark/scripts/sql-batch.py`: one `POST …/sql/execute` of 250 parameterized `INSERT`s (100-byte text), then one
`UPDATE` of those 250 rows, per round, into a table the run creates and drops. Target: a 250-row batch commits in under
1 s on prod.

## Before: prod gru, main before this change (2026-10-23 22:43Z)

Database `sqlidx` (home gru, replicas fra and sjc), client mariogk-dev, 12 rounds:

| Request (250 rows) | Rounds | Median s | Min s | Max s | Median ms/row |
| --- | --- | --- | --- | --- | --- |
| 250 INSERT statements | 12 | 1.707 | 0.593 | 4.785 | 6.83 |
| 1 UPDATE of 250 rows | 12 | 0.553 | 0.348 | 4.428 | 2.21 |

The QA report of 21:20Z measured about 4.5 s per 250-row batch on the build deployed before the 21:30Z rollout.

## Local: the Fast-suite server (`DocumentApi`), 46 cores, Release

The same 250-row `INSERT` request against an in-process server (temporary timing test, not committed), steady-state rounds:

| Build | 250 INSERT statements, s |
| --- | --- |
| main | 1.09 to 1.33 |
| this change | 0.42 to 0.56 |

What remains per row: the engine's `PutAsync` (about 0.5 ms locally, 40% of the request), the document service's
version read, one size read, and the commit (about 0.1 s per request).

## After: prod gru, batch 6 (TerraBase b8b40c408, includes #535), 2026-10-24 10:20Z

Fresh database `sqlidx` after the batch-6 wipe (home gru, replicas fra and sjc), client mariogk-dev, 12 rounds:

| Request (250 rows) | Rounds | Median s | Min s | Max s | Median ms/row |
| --- | --- | --- | --- | --- | --- |
| 250 INSERT statements | 12 | 3.074 | 1.507 | 3.890 | 12.29 |
| 1 UPDATE of 250 rows | 12 | 2.676 | 1.793 | 3.669 | 10.70 |

Smaller requests in the same session: `SELECT 1` 0.13 s, a 1-row `INSERT` 0.20 s, a 10-row batch 0.22 s (medians).
Request size against time, two passes:

| Rows | Pass 1, s | Pass 2, s |
| --- | --- | --- |
| 25 | 0.41 | 1.19 |
| 50 | 0.43 | 0.89 |
| 100 | 0.85 | 3.39 |
| 250 | 5.73 | 2.82 |

## Reading

- The SQL layer's own per-row work fell by about 2.5x (local rows). Prod does not show it: the same request varies by
  2 to 4 times between consecutive runs, and the after run on the freshly wiped cluster is slower than the before run.
- What remains per row is in the engine write path (the document version read, `PutAsync`, the size read and the
  commit) and in prod load, not in SQL parsing or definition reads.
- The target (a 250-row batch under 1 s on prod) is not met. The next step belongs to the write path's owner:
  profile `PutAsync` and commit for a 250-write transaction on gru.
