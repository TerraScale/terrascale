# Sync engine local encryption (Q-ENC), 2026-11-03

Patch: `patches/0007-sync-engine-local-encryption.patch`, applied after 0006.

Region replicas open their sync database with a local aegis256 key. Before this
patch only the connection the SDK opened carried the key; the connections the
sync engine opens itself did not, and the client staged pull answers raw. Replica
`-wal` and `-changes` files held plaintext pages.

| File | Before | After |
| --- | --- | --- |
| `<db>` | encrypted | encrypted; bootstrap writes pages through the replica's page codec |
| `<db>-wal` | plaintext frames from the apply connection | every engine connection (tape, WAL session, replay, reserved-bytes setup) connects with the key |
| `<db>-wal-revert` | plaintext | the revert database opens with the same encryption options and key |
| `<db>-changes` | plaintext pull answer | each staged page is sealed with the replica's codec before staging and opened on apply |

Staged pages reuse the reserved bytes the database already keeps for the cipher
metadata, so frame sizes are unchanged. A page without zeroed reserved bytes, or a
page 1 without an SQLite header, is refused instead of staged in plaintext. With
local encryption, the legacy protocol, MVCC logical-log pulls and partial-sync
bootstrap are refused; TerraBase serves only V1 page pulls. The engine logs only
the cipher name, never the key.

Unit tests of `turso_sync_engine` at the pinned toolchain: 110 passed, 6 failed,
1 ignored, both with and without 0007. The six failures predate 0007: their test
fixtures still query `turso_cdc`, which 0006 renamed.

## Evidence

Release: [`turso-native-v0.8.2-fts-contract1-aegis-wal1-interrupt-synccdc1-localenc1-upsertcdc1-20261104`](https://github.com/TerraScale/terrascale/releases/tag/turso-native-v0.8.2-fts-contract1-aegis-wal1-interrupt-synccdc1-localenc1-upsertcdc1-20261104), patches 0001-0008,
built by [maintenance run 41767214589](https://github.com/TerraScale/terrascale/actions/runs/41767214589):
two independent byte-identical builds per real architecture, FTS, contract1 and WAL smokes passed. The earlier
0001-0007 release `turso-native-v0.8.2-fts-contract1-aegis-wal1-interrupt-synccdc1-localenc1-20261104` (run 41745677415) is superseded and was never pinned; its `PROVENANCE.txt` named the
2026-10-29 release because the assembly script hardcoded the tag.

| Architecture | SHA-256 |
| --- | --- |
| linux-x64 | `b4821db592c5fd0b55b9b5f3e4cc443c48a9e7d3b7b6c6b1370c667cbbbfb776` |
| linux-arm64 | `609d0735b62433f9011d2417b6bc3ff3f7a1babe66f1555e7cf008f7b75446e3` |

On the exact x64 bytes: driver 25/25, TerraBase Fast 424/424, and with R3 replicas and local encryption both
RegionReplicaTests pass with no plaintext marker in any replica file. On the previous release the replica test
leaves the marker in `<db>.db-wal` (296,672 bytes) and `<db>.db-changes` (218,360 bytes), or, with client
bootstrap, fails to decrypt page 1.

## Patch 0008

Upstream Turso 26dbb464 (fixes tursodatabase/turso#9069), `core/translate/upsert.rs` only: `ON CONFLICT DO UPDATE`
read the CDC before image after overwriting the row, so `full` and `before` capture recorded the new row. The
replica sync client captures in `full` mode. `CdcUpsertTests` in the driver suite fails on the previous release
(before image equals the new row) and passes on this one.
