# WAL revision contract build (PIVOT R1), 2026-10-29

Release: [`turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029-r2`](https://github.com/TerraScale/terrascale/releases/tag/turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029-r2).
It is the 2026-10-23 FTS + contract1 + runtime AEGIS build plus one patch,
`patches/0004-explicit-wal-revision-contract.patch`, which the durable sync
revision layer needs (PIVOT D6, R1). The first build of this contract,
`turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029`, is superseded and never
pinned: review found that watermark reads could abort the process, frame ranges
were unchecked, and WAL state inside a write transaction showed uncommitted spill
frames.

## Contract

| Export | What it does |
| --- | --- |
| `turso_terrascale_wal_contract()` | Returns 1. The managed driver refuses the calls below without it. |
| `turso_terrascale_wal_manual(conn)` | Turns off every automatic WAL action on that connection: the checkpoint after a commit, the WAL header restart before a write, and the checkpoint when the last connection closes. Only explicit checkpoints remain. |
| `turso_terrascale_wal_state(conn, *max_frame, *checkpoint_seq)` | The frame count and checkpoint sequence of the connection's last snapshot. Refused inside a write transaction. |
| `turso_terrascale_wal_frame_pages(conn, first, count, *pages, *db_sizes)` | For each frame, the page it wrote and the database size it committed (0 for a frame that is not a commit). Refused inside a write transaction, past the last frame, on overflow or with overlapping buffers. |
| `turso_terrascale_snapshot_read_page(conn, page, *buf, len, *found)` | The decrypted page as of the caller's open read transaction. Refused without one. |
| `turso_terrascale_page_size(conn)` | The page size, which `buf` must match. |

A connection that does not call `turso_terrascale_wal_manual` behaves exactly as
in the 2026-10-23 build. Page reads use the read transaction's snapshot, which a
concurrent checkpoint cannot move, so no caller-chosen watermark reaches Turso's
frame lookup.

## Evidence

[Maintenance run 40926071472](https://github.com/TerraScale/terrascale/actions/runs/40926071472)
built twice per real architecture at `1794cd71883ac8231368371f76b9bfb98a98e445`.
Each pair is byte-identical, and every build passed the FTS, contract1 and WAL
smokes (`scripts/vendor/turso-native-wal-smoke.c`). On an aegis256 database the
WAL smoke proves that 1,519 frames leave the database file's size and mtime
untouched through close, so no checkpoint of any kind ran; that a reader's
snapshot keeps the old page through an update and a passive checkpoint, then
sees the new page in a new snapshot; that write transactions, reads without a
snapshot, past-the-end and overflowing frame ranges, overlapping buffers and a
wrong buffer length are refused; and that an explicit `TRUNCATE` resets the frame
count while every page stays readable.

| Architecture | SHA-256 |
| --- | --- |
| linux-x64 | `1431de682a82a2634787acb69811627f5a9ce1ece5db5be5c490496f6c34bae3` |
| linux-arm64 | `bf26e0fa440fc20cc8c28510d2d7226fb34811f7bf74cd6d051a4cecab8fe87f` |

Normal CI on the pinning PR runs the driver and TerraBase Fast suites against
these exact bytes.
