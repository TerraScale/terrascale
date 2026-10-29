# WAL revision contract build (PIVOT R1), 2026-10-29

Release: [`turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029-r4`](https://github.com/TerraScale/terrascale/releases/tag/turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029-r4).
It is the 2026-10-23 FTS + contract1 + runtime AEGIS build plus one patch,
`patches/0004-explicit-wal-revision-contract.patch`, which the durable sync
revision layer needs (PIVOT D6, R1). Earlier builds of this contract
(`-20261029`, `-r2`) are superseded and never pinned; `-r3` was never published.
Reviews found that watermark reads could abort the process, frame ranges were
unchecked, WAL state mixed a new checkpoint sequence with an old frame count,
idle frame reads could see another connection's uncommitted spills, and
`PRAGMA wal_checkpoint` reports hard checkpoint errors as a busy row.

## Contract

| Export | What it does |
| --- | --- |
| `turso_terrascale_wal_contract()` | Returns 1. The managed driver refuses the calls below without it. |
| `turso_terrascale_wal_manual(conn)` | Turns off every automatic WAL action on that connection: the checkpoint after a commit, the WAL header restart before a write, and the checkpoint when the last connection closes. Only explicit checkpoints remain. |
| `turso_terrascale_wal_state(conn, *max_frame, *checkpoint_seq)` | The connection's frozen snapshot pair: frame count and checkpoint sequence from the same snapshot. Refused inside a write transaction. |
| `turso_terrascale_wal_frame_pages(conn, first, count, *pages, *db_sizes)` | For each frame, the page it wrote and the database size it committed (0 for a frame that is not a commit). Only inside a read transaction and within its snapshot; refused on overflow or overlapping buffers; fails if the WAL restarted before or during the read. |
| `turso_terrascale_wal_checkpoint_truncate(conn, upper_bound, *truncated)` | A TRUNCATE checkpoint bounded by `upper_bound`, outside any transaction. Busy readers or a stale bound leave the WAL in place (`truncated = 0`); every other failure, I/O included, is an error. |
| `turso_terrascale_snapshot_read_page(conn, page, *buf, len, *found)` | The decrypted page as of the caller's open read transaction. Refused without one. |
| `turso_terrascale_page_size(conn)` | The page size, which `buf` must match. |

A connection that does not call `turso_terrascale_wal_manual` behaves exactly as
in the 2026-10-23 build. Page reads use the read transaction's snapshot, which a
concurrent checkpoint cannot move, so no caller-chosen watermark reaches Turso's
frame lookup.

## Evidence

[Maintenance run 40932252046](https://github.com/TerraScale/terrascale/actions/runs/40932252046)
built twice per real architecture at `3d097813f42793352a3b23ebcbf9f437c8846057`.
Each pair is byte-identical, and every build passed the FTS, contract1 and WAL
smokes (`scripts/vendor/turso-native-wal-smoke.c`). On an aegis256 database the
WAL smoke proves that:

- 1,519 frames leave the database file's size and mtime untouched through close, so no checkpoint of any kind ran;
- a reader's snapshot keeps the old page through an update and a passive checkpoint, and a new snapshot sees the new page;
- a write transaction, a read without a snapshot, another connection's TRUNCATE and uncommitted spills, past-the-end and overflowing frame ranges, overlapping buffers and a wrong buffer length are all refused;
- a truncate blocked by a reader or bounded by a stale frame keeps the WAL, and a clean bounded truncate resets the frame count while every page stays readable.

| Architecture | SHA-256 |
| --- | --- |
| linux-x64 | `39dc75c6662f51b2e1ca386f13226530592b116498f6353f00cf02b7592c2a9c` |
| linux-arm64 | `c95481717a451c040fb9b4f435abdaf5a01658ed0e2f08d707b87c5ee962d1ac` |

Normal CI on the pinning PR runs the driver and TerraBase Fast suites against
these exact bytes.
