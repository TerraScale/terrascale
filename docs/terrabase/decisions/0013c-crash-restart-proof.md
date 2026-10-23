# 0013c: A copy written at an unclean exit serves on from its own log once proven

- Status: proposed (RT2 lane G, finding F1, 2026-10-23; awaiting human review)
- Scope: `src/TerraBase/Engine/Sqlite` (`RebuildLedger.RecoverAsync`, `PageCache`). Narrows the write-intent rule that
  closes the open gap of [0013a](0013a-remote-durable-acknowledgement.md) section 1.3 ("rebuilt or proven").

## Context

Write intents close the 0013a gap: a process that dies after a failed sync but before its fault record could restart
on the same boot with the unsynced pages still in the kernel's page cache and certify them. Every intent left by an
unclean exit became a copy fault. So any SIGKILL or power loss during ordinary writes left the copy answering
`503 copy_faulted` until a peer rebuilt it: a single node never recovered, and in a cell every database written at the
crash needed a full refill. 13 E2E crash tests failed this way.

Every write transaction commits with `synchronous = FULL` (B1), so the copy's own log already holds every acknowledged
write. Only the page cache can make the copy differ from its disk.

## Decision

At the next start, an intent of another replica id leaves its copy in service, with no record, when all of these hold:

1. No rebuild record names the database. A copy under rebuild, or one whose certified rebuild is not retired, faults as
   before.
2. No fault of the copy was being recorded. Noting a fault in memory marks its intent `Faulting` at once (replica 0,
   which no replica id uses); the record's commit clears the mark. A marked intent always faults the copy.
3. The kernel caches none of the copy's pages: each of its files (database and sidecars) syncs without error, its
   cached pages are dropped (`POSIX_FADV_DONTNEED`), and `mincore` over a mapping of the whole file finds none resident.
   Reads then come from the disk alone, exactly as after a power loss, which FULL commits survive.

Anything else (another platform, a sync error, a page still cached or mapped) faults the copy as before.

## Consequences

- Ordinary writes interrupted by SIGKILL, abort or power loss recover locally from the log. The torn, unacknowledged
  transaction is discarded by log recovery.
- A crash in the short window between a detected fault (failed sync, corruption) and its `Faulting` mark behaves like
  a crash just before detection: a failed sync is covered by the cache proof, and corruption on disk is detected again
  when it is read.
- No on-disk format change; old and new binaries read the journal the same way (an old binary faults a marked intent).
- Not proven here: whether a commit can become readable (and replicate) before its sync completes. If it can, a power
  loss can drop a change a peer already holds, and anti-entropy, not the change log, converges it.
