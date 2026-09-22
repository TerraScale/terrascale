# Filesystem-full qualification

| Check | Result |
| --- | --- |
| Filesystem | Dedicated tmpfs, 16,777,216 bytes |
| Seed | 100 synced records, compacted to an external filesystem object store |
| Full-filesystem WAL append | No acknowledgement; unknown outcome, underlying HRESULT 0x0000001C |
| Reopen after freeing filler | 100 acknowledged bodies/versions exact |
| Unacknowledged request reconciliation | Committed, exact body and committed receipt verified |
| Full-filesystem segment staging | Rejected, HRESULT 0x0000001C |
| Failed compaction manifest | Byte-for-byte unchanged |
| Retry after freeing space | Compaction and second exact reopen passed |

This is real Linux ENOSPC evidence for local WAL and segment staging. Object
storage used the filesystem fixture outside the quota; this is not a RustFS
disk-full, device power-loss, or larger-than-memory performance qualification.