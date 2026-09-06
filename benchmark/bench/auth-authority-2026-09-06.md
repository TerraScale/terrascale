# Auth authority diagnostic timings

These are diagnostic test durations from a local debug build, using the public
TypeScript SDK over HTTP against two encrypted SQLite voters and authenticated
QUIC. They include fixture work, record creation, assertions, and cleanup; they
are not per-query latency or production capacity measurements. The maintained
Better Auth utility is pinned to 1.7.2 and uses the separately documented,
hash-checked cleanup-only diagnostic patch. No immutable release is qualified
by this table.

The repeated case is `findMany - should find many models with join`: ten users,
thirty sessions, thirty accounts, the join assertion, and durable cleanup.

| Working-tree behavior / run | Normal case | Joins case | UUID case | Result |
| --- | ---: | ---: | ---: | --- |
| FIFO auth RPC admission; full suite (`37693`) | 13.784 s | 24.026 s | >30 s timeout | 271 passed, 1 failed, 78 unexecuted |
| Same log reader; three isolated cases with timing (`89330`) | 13.015 s | 14.108 s | 15.549 s | 3 passed, 347 deliberately filtered; not qualification |
| Indexed log-range reader; full suite (`64296`) | 12.711 s | 12.840 s | 12.226 s | 350 passed, 0 failed, 0 skipped |

Logs: `/tmp/terrascale-auth-fifo-diagnostic.log` and
`/tmp/terrascale-auth-join-timing.log`. The isolated run logged no local query
operation above 10 ms. Its logged WAL compactions totalled 5.81 seconds across
30 events; overlapping store/network timers must not be added together.

A subsequent full timing run (`1646`) was deliberately terminated after
identifying that Raft log-range reads decoded every retained log row. Its
180.31-second execution is diagnostic only (Node exit 143, Rust exit 101), not
a test pass or a newly discovered product failure. Log:
`/tmp/terrascale-auth-full-timing.log`.

The corrected reader uses the existing primary-key range in SQL. Its tests
cover every bound form, unsigned extrema, empty ranges, malformed rows outside
and inside the requested range, key/entry mismatch, and SQLite's bounded index
search plan. The full cleanup-patched suite passed in 325.26 seconds (Vitest 324.35 seconds),
without fixture resets, read retries, relaxed assertions, or changed timeouts.
No indexed log read exceeded the 10 ms diagnostic logging threshold. Log:
`/tmp/terrascale-auth-indexed-log-diagnostic.log`. This is the patched-cleanup
qualification label; the unmodified upstream utility gate remains unpassed.
