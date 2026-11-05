# R13 router differential: local mode vs home-only (2026-11-05)

`tests/TerraBase.Slow/Sync/RouterDifferentialTests.cs`, run with `make terrabase-test-slow` through `ts-heavy-any` on beta.
Source: main 959e6055d plus the replica fixes of the R13 commit and the test (branch `r13-test` @ dfc1e7ab5).

One client runs the same 10,000 random transactions (1 to 3 statements each, over 5 tables and 48 keys per table) in
order against two databases homed in gru:

- **local mode**: SQL REST in sjc with `TerraScale-Durability: home`. The router (R4) commits the writes it routes local
  in sjc and pushes them to gru (R7, applied by R6 with HLC LWW); the rest is forwarded home (R5) behind the region's
  pushes (push-through fence).
- **home-only**: the same transactions at gru.

A difference is a row whose final value at gru differs between the two databases; it is silent when no
`terrascale.conflicts` record names it. Region drift is a row whose value in sjc's replica differs from gru's after the
push settles.

| Repeat | Seed | Wall time | Committed in sjc | Forwarded | Failed (local / home-only) | Rows | Conflicts | Differences | Silent | Region drift |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 1797936033 | 200.0 s | 3268 | 1900 | 4832 / 4832 | 170 / 170 | 0 | 0 | **0** | 0 |
| 2 | 1401991755 | 202.6 s | 3292 | 1825 | 4883 / 4883 | 170 / 170 | 0 | 0 | **0** | 0 |
| 3 | 3442524 | 219.3 s | 3263 | 1839 | 4898 / 4898 | 167 / 167 | 0 | 0 | **0** | 0 |

Failed transactions are refused by a constraint (an insert of an existing key, a child of a missing parent, a duplicate
email). They fail identically in both modes: the local ones are reverted at gru and answer the home's error.

## Before the fixes (same harness, earlier revisions)

| Run | What it found | Fix |
|---|---|---|
| seed 373587415 | 24 silent of 35 differences: a forwarded write overtook the region's unapplied local pushes (`n = n + k` on a row whose local insert had not reached gru) and a local write after a forwarded one ran on a replica without it | push-through fence and pull fence (R7, e93dcdf4e) |
| first 10k runs | a replica read failed with `database is locked` while the replica's writer committed | reader BEGIN waits out the lock (R13 commit) |
| first 10k runs | R11 verification saw `_ts_local_cdc` and overlay rows as divergence and rebootstrapped the replica under a live local write (`database is invalid`) | verification skips replicas holding local writes and node-only objects (R13 commit) |

Wall time is bounded by sjc's request budget: sjc holds no plan for an organization homed elsewhere and admits it as
Hobby (50 requests/s), so the client honors `Retry-After`.
