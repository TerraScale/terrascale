# WAL revision contract build (PIVOT R1), 2026-10-29

Release: [`turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029`](https://github.com/TerraScale/terrascale/releases/tag/turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029).
It is the 2026-10-23 FTS + contract1 + runtime AEGIS build plus one patch,
`patches/0004-explicit-wal-revision-contract.patch` (SHA-256
`7fa6361646a882508b7825dc59237245fc6a5a87d9f85c9718b5af19b485808f`), which the
durable sync revision layer needs (PIVOT D6, R1).

## Contract

| Export | What it does |
| --- | --- |
| `turso_terrascale_wal_contract()` | Returns 1. The managed driver refuses the calls below without it. |
| `turso_terrascale_wal_manual(conn)` | Turns off every automatic WAL action on that connection: the checkpoint after a commit, the WAL header restart before a write, and the checkpoint when the last connection closes. Only explicit checkpoints remain. |
| `turso_terrascale_wal_state(conn, *max_frame, *checkpoint_seq)` | The connection's committed WAL frame count and checkpoint sequence. |
| `turso_terrascale_wal_frame_pages(conn, first, count, *pages, *db_sizes)` | For each frame, the page it wrote and the database size it committed (0 for a frame that is not a commit). |
| `turso_terrascale_wal_read_page(conn, page, watermark, *buf, len, *found)` | The decrypted page as of a WAL frame watermark. |
| `turso_terrascale_page_size(conn)` | The page size, which `buf` must match. |

A connection that does not call `turso_terrascale_wal_manual` behaves exactly as
in the 2026-10-23 build.

## Evidence

[Maintenance run 40923694386](https://github.com/TerraScale/terrascale/actions/runs/40923694386)
built twice per real architecture at `577755465fecf44456720a6266fa3facd13ab796`.
Each pair is byte-identical, and every build passed the FTS, contract1 and new
WAL smokes (`scripts/vendor/turso-native-wal-smoke.c`). The WAL smoke, on an
aegis256 database, proves: no automatic checkpoint over 1,519 frames, the WAL
kept across close and reopen, page reads at an older and a newer watermark
return the matching decrypted content, a wrong buffer length is refused, and an
explicit `TRUNCATE` resets the frame count while every page stays readable.

| Architecture | SHA-256 |
| --- | --- |
| linux-x64 | `efcca70f6475a4c40bdcc88fff14a4c961f0ee4022ae6a7f775d6fc506b7b002` |
| linux-arm64 | `8d71a1eae9d899db410bd0b2dcf046fe622e160819ca61da719175bf335141ab` |

Normal CI on the pinning PR runs the driver and TerraBase Fast suites against
these exact bytes.
