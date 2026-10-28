# 27. Idle databases cost almost nothing between writes

Amends the frontier cadence of [R7 as built](21-r7-as-built.md) and the retention pass of
[sender ACK bookkeeping](21-sender-ack-bookkeeping.md). No protocol, storage format or durability change.

**Problem.** On production (2026-10-27, revision `9731313c0`, almost no client traffic) each node took its writers about
30 times a second for databases nobody wrote: each idle database sent every peer a frontier every 5 s, which the peer
recorded in a FULL write transaction (`RecordFrontierAsync`, 11-13 per second per node) and answered with a stable report
the origin recorded in another (12-18 per second), and each ran a retention pass every 5 s under its writer (6-7 per
second, about 18 ms each). The catalog's closure convergence opened a write transaction per settled project closure every
2 s (17-27 per second). The sender loop also made about 2,000 empty passes per
second: timers fire on a coarse clock, often just before a deadline, so the pass found nothing due and the loop spun until
it was. Client writes and replicated batches queued behind all of it on 2-CPU hosts.

**Rules.**

- **Frontiers back off.** A database's first frontier after a batch goes 5 s after the last one, as before; each further
  frontier with nothing shipped in between, after the peer answered the last one with its stable report, doubles the wait,
  up to 60 s. A frontier the peer dropped (behind its database's work, or past its 32 databases in progress) is repeated
  at 5 s, as before. Shipping a batch, or a reset cursor, starts again
  at 5 s. A frontier only raises the peer's `applied` stamp for that origin, which feeds `stable`, the collection horizon
  (stable minus 60 s) and snapshot coverage: an idle origin's stable stamp now trails by at most 60 s instead of 5 s.
  Acks of batches carry `stable` as before, so a database being written keeps its 5 s cadence.
- **Retention passes back off.** A database with no commit since its last pass began, no ack recorded since, and no trim
  batch left over is passed at most every 60 s instead of every 5 s. Its time-based cuts (24 h, far behind) and the
  collection horizon therefore move up to 60 s later; acks, commits and unfinished trims keep the 5 s cadence.
- **Settled closures are read, not written.** A closure whose machine receipt already records it as empty, with every
  waited-for purge verified and nothing left to erase, is confirmed in a read transaction. Only a closure with work left
  takes the catalog's writer, exactly as before; the check reads what that write transaction would have found.
- **Deadlines within a timer tick are due.** The sender treats an item, its retry and its frontier as due within 15 ms of
  the deadline, and waits zero when the first deadline is that close.
- `terrabase.storage.transactions{kind, at}` counts begun storage transactions by call site (file:line, as
  `terrabase.storage.writer_held`), so the next hot loop is visible in production.
