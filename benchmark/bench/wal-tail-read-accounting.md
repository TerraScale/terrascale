# WAL tail read accounting

The tailer now seeks past its shipped prefix before reading frames. These
counts describe bytes returned by the reader; filesystem caching determines
the physical disk I/O.

| Poll state | Previous reader bytes | Current reader bytes |
| --- | ---: | ---: |
| Idle, complete committed WAL | Entire WAL length | 32 |
| New committed frames after an acknowledged prefix | Entire WAL length | 32 + bounded frame batch + commit lookahead when needed |
| Pending unacknowledged batch | 0 | 0 |

The regression
`wal_follower::tests::idle_poll_reads_only_header_and_catchup_reads_only_new_frames`
creates a real SQLite WAL containing a one-megabyte value, acknowledges its
prefix, and counts reader bytes for idle and catch-up polls. It verifies the
32-byte idle read and exact header-plus-batch catch-up read. Its small follow-up
transaction produces a batch below 64 KiB.

The 16 TBEP WAL follower tests cover durable acknowledgements, generation
changes, uncommitted spills, corruption, restart, and promotion. Capture still
requires the engine's commit/checkpoint barrier.

Frame payloads are capped at one MiB per batch. A transaction larger than that
cap is checked for a commit marker with bounded scan buffers before any of its
frames ship. A confirmed boundary is cached for subsequent batches. The tail
keeps only the latest commit marker, so its checkpoint metadata does not grow
with the number of commits. Partial-batch acknowledgements retain the earlier
committed prefix until the final commit frame arrives.

The large-transaction regression ships a three-megabyte value in bounded
batches, restarts the follower after a partial batch, resumes from its recovered
checkpoint, and verifies the complete promoted value. It also rejects oversized
incoming batches. Scan I/O and capture-barrier duration still depend on the
distance to a commit marker.

Bootstrap, follower reopen, and fold verification now scan with the same
one-MiB maximum frame buffer. They retain only the latest committed checkpoint.
Bootstrap streams that verified prefix to the staged WAL instead of retaining
a second full-file copy. The instrumented reader checks the maximum scan read
request against the cap on a WAL larger than one MiB; the integration regression
bootstraps, reopens, folds, and reads back a three-megabyte value. SQLite's own
integrity-check memory and filesystem cache are outside these buffer counts.
The production QUIC adapter remains unfinished.
