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

Release: [`turso-native-v0.8.2-fts-contract1-aegis-wal1-interrupt-synccdc1-localenc1-20261104`](https://github.com/TerraScale/terrascale/releases/tag/turso-native-v0.8.2-fts-contract1-aegis-wal1-interrupt-synccdc1-localenc1-20261104),
built by [maintenance run 41745677415](https://github.com/TerraScale/terrascale/actions/runs/41745677415):
two independent byte-identical builds per real architecture, FTS, contract1 and WAL smokes passed. Its
`PROVENANCE.txt` names the previous release because the assembly script still hardcoded that tag; the script now
names this one.

| Architecture | SHA-256 |
| --- | --- |
| linux-x64 | `066d730867a9c0b6bb6f48cba1ea312e2cace345972f7aebbc93c8b820856db8` |
| linux-arm64 | `5ae94d9d2bd2da7f62acf0f9736c8503f7dbdcd63ac3cbf94ac8f4fe38cb613b` |

On the exact x64 bytes with R3 replicas and local encryption: driver 24/24, TerraBase Fast 423/423. The replica
test finds no plaintext marker in any replica file. The same test on the previous release leaves the marker in
`<db>.db-wal` (296,672 bytes) and `<db>.db-changes` (218,360 bytes), or, with client bootstrap, fails to decrypt
page 1.
